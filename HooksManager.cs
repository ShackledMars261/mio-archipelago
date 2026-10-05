using MioGame;
using MioModdingApi;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MioAP
{
    /// <summary>
    /// Bridges the game to Archipelago by intercepting the save layer.
    ///
    /// Two ideas carry most of the weight:
    ///
    /// SHADOW ENTRIES. A vanilla save entry such as "UNLOCK:HOOK" means two
    /// things at once - "the player can use the hook" and "the hook pickup has
    /// been taken". A randomizer breaks that equality, so the mod invents a
    /// second entry, "ARCHIPELAGO_UNLOCK:HOOK", to carry the second meaning.
    /// The real entry stays the source of truth for possession and is written
    /// only when Archipelago actually sends the item.
    ///
    /// SCOPES. Which of those two meanings a given Mio::has call wants depends
    /// entirely on who is asking. A locked door asking "do you hold this key"
    /// wants the real entry; a pickup asking "have I been taken" wants the
    /// shadow. We can't read the native caller from a managed hook, so instead
    /// we hook the callers we care about and set a thread-local scope for the
    /// duration of the original call. Mio::has consults that scope to decide.
    /// The C++ client did the same thing by matching return addresses, which
    /// isn't available to us - a managed hook's return address points into the
    /// trampoline, not game code.
    /// </summary>
    internal class HooksManager
    {
        // ---------------------------------------------------------------
        // Dependencies
        // ---------------------------------------------------------------

        private readonly Action<string> _loggingCBMethod;
        private readonly DataManager dataManager;
        private readonly ArchipelagoManager apManager;
        private readonly Action<string> _showToast;

        /// <summary>
        /// Enables the caller-attribution diagnostics: the symbol table, stack
        /// scanning, and the [has-unscoped] / [loot1] / [popup] logs. These exist
        /// to find pickup call sites that still need a scope.
        ///
        /// This is the one to flip while debugging. A Release build forces
        /// Diagnostics off regardless of what it says here, so the cost can't
        /// reach players: the symbol table alone is ~11k reflection reads at
        /// startup, and the stack scan runs on every Mio::has call.
        /// </summary>
        private const bool DiagnosticsWanted = true;

#if DEBUG
        private const bool Diagnostics = DiagnosticsWanted;
#else
        private const bool Diagnostics = false;   // never ships on
#endif

        // ---------------------------------------------------------------
        // Save entries and categories
        // ---------------------------------------------------------------

        private const string NacreEntry = "RESOURCE:SOLID_DROPLETS";      // Crystallized Nacre
        private const string LiquidNacreEntry = "RESOURCE:PEARL_SHARDS";  // Liquid Nacre
        private const string FullPearlsEntry = "RESOURCE:FULL_PEARLS";    // unspent Old Cores
        private const string ExtraShieldsEntry = "RESOURCE:EXTRA_SHIELDS";

        private const string CarcassCategory = "CARCASS";
        private const string ShieldFragmentCategory = "SHIELD_FRAGMENT";
        private const string TrinketSlotUpgradeCategory = "TRINKET_SLOT_UPGRADE";

        /// <summary>
        /// How many AP items we've applied, stored in the save so it survives a
        /// reload. Items arrive in a stable order, so "applied through index N"
        /// makes grants exactly-once even for filler that can arrive repeatedly.
        /// </summary>
        private const string WatermarkEntry = "ARCHIPELAGO_META:ITEM_INDEX";

        // ---------------------------------------------------------------
        // Tuning
        // ---------------------------------------------------------------

        /// <summary>Range granted per Nacre filler item.</summary>
        private const int NacreMin = 20;
        private const int NacreMax = 200;

        /// <summary>Trinket capacity with no upgrades, and the gain per upgrade.</summary>
        private const uint TrinketSlotsBase = 25;
        private const uint TrinketSlotsPerUpgrade = 10;

        /// <summary>Shield fragments needed for one extra shield.</summary>
        private const int FragmentsPerShield = 4;

        private static readonly Random _rng = new();

        /// <summary>
        /// Save entries that need an explicit count written after acquire(), and
        /// the value to write. A key may be a full entry ("ATTACK_POWER:1") or a
        /// category prefix ("ATTACK_POWER") covering every entry in it; a full
        /// entry wins over a category.
        ///
        /// Most entries ignore count - abilities and door keys work at 0, where
        /// existence alone means the player has it. These categories store "how
        /// many you have" in count, so it has to be set explicitly.
        ///
        /// Counts are SET rather than incremented, so writing the same entry
        /// twice is idempotent.
        /// </summary>
        private static readonly Dictionary<string, int> GrantCounts = new(StringComparer.Ordinal)
        {
            { "ATTACK_POWER",         1 },
            { "SHIELD_FRAGMENT",      1 },
            { "TRINKET_SLOT_UPGRADE", 1 },
            { "CANDLE",               1 },
            { "CARCASS",              1 },
        };

        // ---------------------------------------------------------------
        // Scope state
        // ---------------------------------------------------------------

        /// <summary>
        /// Save entries whose meaning is currently "this pickup was consumed"
        /// rather than "the player has the item". Null outside any scope.
        /// </summary>
        [ThreadStatic] private static string[]? _scopeEntries;

        /// <summary>Name of the active scope, for diagnostics and per-site identity.</summary>
        [ThreadStatic] private static string? _scopeName;

        /// <summary>
        /// Sentinel scope meaning "every AP-managed item". Inside a generic
        /// pickup system, any AP item id is asking about pickup state, so there
        /// is no point enumerating them. Compared by reference, so it must stay
        /// a distinct instance.
        /// </summary>
        private static readonly string[] ScopeAllApItems = Array.Empty<string>();

        // Single-purpose call sites. Static arrays so entering a scope allocates
        // nothing. An entry ending in ':' matches the whole category by prefix.
        private static readonly string[] ScopeHook = { "UNLOCK:HOOK" };
        private static readonly string[] ScopeManaReset = { "UNLOCK:HIT_RECHARGE" };
        private static readonly string[] ScopeBlock = { "UNLOCK:BLOCK" };
        private static readonly string[] ScopeSpider = { "UNLOCK:SPIDER" };
        private static readonly string[] ScopeGlide = { "UNLOCK:GLIDE" };
        private static readonly string[] ScopeOrbShoot = { "UNLOCK:ORB_SHOOT" };
        private static readonly string[] ScopeMap = { "UNLOCK:MAP" };

        /// <summary>Npc_node::update_hacker is one handler shared by several unlocks.</summary>
        private static readonly string[] ScopeHacker =
        {
            "UNLOCK:HOOK", "UNLOCK:HIT_RECHARGE", "UNLOCK:BLOCK",
            "UNLOCK:GLIDE", "UNLOCK:SPIDER", "UNLOCK:ORB_SHOOT", "UNLOCK:SPIDER_GOO",
        };

        /// <summary>
        /// Mel's offers, by the save entry of their vanilla reward. Narrow
        /// rather than ScopeAllApItems because an offer's condition tests two
        /// different things: whether the offer was bought (its own id, which
        /// must read the shadow) and whether the player owns a prerequisite
        /// (which must read the real entry). Scoping only the offers' own
        /// entries keeps both correct, and stays correct if the scraplings
        /// ever enter the item pool.
        /// </summary>
        private readonly string[] _scopeShopOffers;

        /// <summary>
        /// A carcass pickup grants its currency in a second loot call - Nacre
        /// bumps SOLID_DROPLETS, an Old Core bumps FULL_PEARLS. When the first
        /// call is intercepted, this holds the entry that follow-up will use so
        /// it can be dropped too.
        ///
        /// Known limitation: a latch with no expiry. If the follow-up never
        /// arrives it stays armed and swallows the next legitimate pickup of
        /// that entry instead.
        /// </summary>
        [ThreadStatic] private static string? _suppressNextLoot;

        /// <summary>The intro sequence room, where being in the glitch world is alright.</summary>
        private const string GlitchZone = "GW_intro_jump_P1";

        /// <summary>The room containing Mel's shop.</summary>
        private const string ShopRoom = "HUB_hub_shop";

        /// <summary>
        /// Shop locations already scouted, so the per-frame grid rebuild
        /// doesn't re-send. Cleared on load, since a different save is a
        /// different multiworld.
        /// </summary>
        private readonly HashSet<int> _hintedShopLocations = new();

        public HooksManager(Action<string> loggingCBMethod, DataManager dataManager, ArchipelagoManager apManager, Action<string> showToastMethod)
        {
            _loggingCBMethod = loggingCBMethod;
            this.dataManager = dataManager;
            this.apManager = apManager;
            _showToast = showToastMethod;

            _scopeShopOffers = dataManager.GetVanillaEntriesInRoom(ShopRoom);
            if (_scopeShopOffers.Length == 0)
                LogMessage($"[hooks] WARNING: no shop offers found in {ShopRoom} - shop scope is inert");
            else
                LogMessage($"[hooks] shop scope: {_scopeShopOffers.Length} offers in {ShopRoom}");
        }

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        // ===============================================================
        // Registration
        // ===============================================================

        public unsafe void InitHooks()
        {
#pragma warning disable CS0162
            if (Diagnostics) BuildSymbolTable();
#pragma warning restore CS0162

            // Per-frame pump for queued Archipelago work.
            On.MioGame.On_Game.fixed_update.Prefix += fixed_update_Prefix;

            // Item and location interception.
            On.MioGame.On_Game.loot_1.Hook += loot_1_Hook;
            On.MioGame.On_Mio.has.Hook += mio_has_Hook;
            On.MioGame.On_Ui_loot_popup.start.Hook += loot_popup_start_Hook;
            On.MioGame.On_Mio.trinket_slots_count.Hook += trinket_slots_count_Hook;

            // Keeps an Archipelago run off the player's vanilla saves.
            On.MioGame.GlobalFunctions.core.On_fmt.stringfv.Hook += stringfv_Hook;

            // Skip Samsk tubes in order to prevent potential soft locks.
            On.MioGame.GlobalFunctions.game.On_game.glitch_state_update.Prefix += Glitch_state_update_Prefix;

            // Sends hints to Archipelago for viewed shop offers.
            On.MioGame.On_Workshop_ui.update_item_grid.Suffix += Update_item_grid_Suffix;

            InitScopeHooks();

            LogMessage("[hooks] installed");
        }

        /// <summary>
        /// One hook per call site whose Mio::has calls mean "has this pickup been
        /// taken". Each sets a scope for the duration of the original call.
        ///
        /// Scopes are dynamic-extent, so hooking an outer function also covers
        /// everything it calls - there's no need to hook the per-entity lambdas
        /// or node methods underneath these systems.
        /// </summary>
        private unsafe void InitScopeHooks()
        {
            // Global systems that walk every entity of a type each frame.
            On.MioGame.On_Game.update_loot.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeAllApItems, "update_loot"); orig(self); };

            On.MioGame.On_Game.update_datapads.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeAllApItems, "update_datapads"); orig(self); };

            On.MioGame.On_Game.update_carcasses.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeAllApItems, "update_carcasses"); orig(self); };

            On.MioGame.On_Game.update_chests2.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeAllApItems, "update_chests2"); orig(self); };

            // Bespoke handlers, each tied to specific unlocks.
            On.MioGame.On_GW_tuto_hook.update_GW_tuto_hook.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeHook, "GW_tuto_hook"); orig(self); };

            On.MioGame.On_GA_manareset.update_hacker_manareset.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeManaReset, "GA_manareset"); orig(self); };

            On.MioGame.On_LQ_block_hacker.update_LQ_block_hacker.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeBlock, "LQ_block_hacker"); orig(self); };

            On.MioGame.On_GA_bou.update_hacker_glide.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeGlide, "GA_bou"); orig(self); };

            On.MioGame.On_LQ_city_hacker.update_TW_mid_hacker.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeSpider, "LQ_city_hacker"); orig(self); };

            On.MioGame.On_GW_tuto_orbshoot.update.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeOrbShoot, "GW_tuto_orbshoot"); orig(self, node); };

            On.MioGame.On_Npc_node.update_hacker.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeHacker, "Npc_node_hacker"); orig(self, node); };

            On.MioGame.On_Hub_general.update_tuner3.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeMap, "Hub_general_tuner3"); orig(self, node); };

            // "Give this if the player hasn't got it" - the guard re-runs every
            // frame, so an unscoped one spins forever on an AP item: we consume
            // the loot and never write the real entry. Scoped, the guard reads
            // the shadow instead and flips the frame after the check is sent.
            On.MioGame.On_Game.loot_up_to_one.Hook +=
                static (orig, self, loot_id, flags) => { using var s = new Scope(ScopeAllApItems, "loot_up_to_one"); return orig(self, loot_id, flags); };

            // Mel's shop. Both the purchase and the grid rebuild run under
            // Workshop_ui.pre_sim_update, reached from Game::fixed_update -
            // Game.update_shops never sees either.
            On.MioGame.On_Workshop_ui.pre_sim_update_all.Hook +=
                (orig, w) => { using var s = new Scope(_scopeShopOffers, "workshop_ui"); orig(w); };

            // Mel's dialog asks whether her stock is empty, which is the same
            // question about the same entries.
            On.MioGame.On_Npc_node.update_mel.Hook +=
                (orig, self, node) => { using var s = new Scope(_scopeShopOffers, "update_mel"); orig(self, node); };
        }

        // ===============================================================
        // Save helpers
        // ===============================================================

        /// <summary>
        /// Count to write for a save entry after acquiring it, or null to leave
        /// whatever acquire() produced. Full entry first, then category prefix.
        /// </summary>
        private static int? CountFor(string saveEntry)
        {
            if (GrantCounts.TryGetValue(saveEntry, out int exact)) return exact;

            int colon = saveEntry.IndexOf(':');
            if (colon > 0 && GrantCounts.TryGetValue(saveEntry[..colon], out int byCategory))
                return byCategory;

            return null;
        }

        /// <summary>
        /// Acquires a save entry and applies its configured count. Returns the
        /// entry so loot hooks can hand it straight back.
        /// </summary>
        private static unsafe Save_entry* AcquireWithCount(ref Game game, string saveEntry)
        {
            MioGame.String s = Util.StringToMioString(saveEntry);
            Save_entry* entry = game.save.acquire(&s);

            int? want = CountFor(saveEntry);
            if (want != null && entry != null) entry->count = want.Value;

            return entry;
        }

        /// <summary>Acquires an entry without touching its count.</summary>
        private static unsafe Save_entry* AcquireRaw(ref Game game, string saveEntry)
        {
            MioGame.String s = Util.StringToMioString(saveEntry);
            return game.save.acquire(&s);
        }

        /// <summary>
        /// How many entries in a category the player actually holds. This is
        /// live player state, unlike CountFor which is what to write on a grant.
        /// </summary>
        private unsafe int HeldInCategory(ref Game game, string category)
        {
            int held = 0;
            foreach (string entry in dataManager.GetEntriesInCategory(category))
            {
                MioGame.String s = Util.StringToMioString(entry);
                if (game.save.acquired_count(&s) > 0) held++;
            }
            return held;
        }

        private static unsafe int ReadWatermark(ref Game game)
        {
            MioGame.String w = Util.StringToMioString(WatermarkEntry);
            return game.save.acquired_count(&w);
        }

        private static unsafe void WriteWatermark(ref Game game, int value)
        {
            Save_entry* entry = AcquireRaw(ref game, WatermarkEntry);
            if (entry != null) entry->count = value;
        }

        // ===============================================================
        // Per-frame pump
        // ===============================================================

        /// <summary>
        /// True when a save is loaded and the world is live. Grants write to the
        /// save, so they must not run at the menu or during splashes. Equivalent
        /// to the C++ API's CheckIfSaveLoaded().
        /// </summary>
        private static bool IsSaveReady()
        {
            try
            {
                ref Metagame meta = ref MioGame.Globals.metagame;
                return meta.state == Metagame.State.Game;
            }
            catch { return false; }
        }

        private unsafe void fixed_update_Prefix(Game* __this)
        {
            try
            {
                // The queues keep filling while we wait, so nothing is lost.
                if (!IsSaveReady()) return;
                apManager.DrainPending(GrantItem, MarkLocationChecked);
            }
            catch (Exception ex) { LogMessage("[hooks] drain failed: " + ex); }
        }

        /// <summary>
        /// Applies an Archipelago item to the save. Game thread only. Anything at
        /// or below the watermark is skipped, which makes reconnect replays and
        /// repeatable filler safe.
        /// </summary>
        private unsafe void GrantItem(int index, Item item, string sender)
        {
            if (string.IsNullOrEmpty(item.SaveEntry)) return;

            ref Game game = ref MioGame.Globals.game;

            if (index < ReadWatermark(ref game)) return;   // already applied

            // Both nacres are spendable filler and can arrive any number of
            // times, which is why grants are keyed on the received-stream index
            // rather than the item id.
            if (item.SaveEntry == NacreEntry || item.SaveEntry == LiquidNacreEntry)
            {
                int amount = _rng.Next(NacreMin, NacreMax + 1);
                Save_entry* balance = AcquireRaw(ref game, item.SaveEntry);
                if (balance != null) balance->count += amount;

                LogMessage($"[AP] +{amount} {item.Name}");
            }
            // Old Cores are spendable currency: FULL_PEARLS is a balance, not a
            // total. Writing CARCASS:X here would tell the game the world pickup
            // is gone and make that location uncheckable, so we deliberately
            // don't - the location's own check writes it instead.
            else if (item.SaveEntry.StartsWith(CarcassCategory + ":", StringComparison.Ordinal))
            {
                Save_entry* balance = AcquireRaw(ref game, FullPearlsEntry);
                if (balance != null) balance->count += 1;

                LogMessage($"[AP] +1 Old Core ({item.Name})");
            }
            else
            {
                AcquireWithCount(ref game, item.SaveEntry);
                SyncDerivedResources(ref game, item.SaveEntry);

                LogMessage($"[AP] granted \"{item.Name}\" ({item.SaveEntry})");
            }

            // Only after a successful apply, so a throw doesn't skip the item.
            WriteWatermark(ref game, index + 1);

            _showToast?.Invoke(string.IsNullOrEmpty(sender)
                ? $"Found {item.Name}"
                : $"Received {item.Name} from {sender}");
        }

        /// <summary>
        /// Marks a checked location's pickup as consumed, so the world stops
        /// offering it even though the player never received that item. Also
        /// runs on reconnect, when the server replays everything already checked.
        /// </summary>
        private unsafe void MarkLocationChecked(Location location)
        {
            ref Game game = ref MioGame.Globals.game;

            // Nacre pickups each have their own per-node carcass entry, which is
            // the game's own "consumed" marker - no shadow needed.
            string? carcass = dataManager.GetCarcassEntryForLocation(location.Id);
            if (carcass != null)
            {
                AcquireWithCount(ref game, carcass);
                return;
            }

            var vanillaItem = dataManager.GetItemById(location.VanillaItem.ItemId);
            if (vanillaItem?.SaveEntry == null) return;

            // Old Core locations likewise use their real CARCASS entry, which is
            // what despawns the world pickup. Everything else uses a shadow.
            string entry = vanillaItem.SaveEntry.StartsWith(CarcassCategory + ":", StringComparison.Ordinal)
                ? vanillaItem.SaveEntry
                : DataManager.ShadowEntry(vanillaItem.SaveEntry);

            AcquireWithCount(ref game, entry);
        }

        // ===============================================================
        // Derived resources
        // ===============================================================

        /// <summary>
        /// Updates any resource whose value is derived from a category of
        /// collectibles. Each sync recomputes from scratch, so calling this more
        /// often than needed is harmless.
        /// </summary>
        private unsafe void SyncDerivedResources(ref Game game, string saveEntry)
        {
            int colon = saveEntry.IndexOf(':');
            if (colon <= 0) return;

            switch (saveEntry[..colon])
            {
                case ShieldFragmentCategory: SyncExtraShields(ref game); break;
            }
        }

        /// <summary>
        /// Recomputes EXTRA_SHIELDS from the fragments actually held. Derived
        /// rather than incremented, so a reconnect replay can't inflate it.
        /// </summary>
        private unsafe void SyncExtraShields(ref Game game)
        {
            int fragments = HeldInCategory(ref game, ShieldFragmentCategory);
            int shields = fragments / FragmentsPerShield;

            Save_entry* entry = AcquireRaw(ref game, ExtraShieldsEntry);
            if (entry != null) entry->count = shields;
        }

        // ===============================================================
        // Scope plumbing
        // ===============================================================

        /// <summary>
        /// Sets the scope for the duration of a block and restores whatever was
        /// there before. Save/restore rather than clear, so nesting works.
        /// </summary>
        private readonly ref struct Scope
        {
            private readonly string[]? _prevEntries;
            private readonly string? _prevName;

            public Scope(string[] entries, string name)
            {
                _prevEntries = _scopeEntries;
                _prevName = _scopeName;
                _scopeEntries = entries;
                _scopeName = name;
            }

            public void Dispose()
            {
                _scopeEntries = _prevEntries;
                _scopeName = _prevName;
            }
        }

        /// <summary>
        /// The shadow entry to answer with for this id right now, or null to let
        /// the real entry through.
        /// </summary>
        private string? ShadowFor(string saveEntry)
        {
            string[]? scope = _scopeEntries;
            if (scope == null) return null;

            // Wildcard: shadow anything Archipelago manages. The nacres are
            // excluded because they're filler that also drops from enemies, so
            // their real entry is always the right answer.
            if (ReferenceEquals(scope, ScopeAllApItems))
            {
                return dataManager.IsApItem(saveEntry)
                    && saveEntry != NacreEntry
                    && saveEntry != LiquidNacreEntry
                        ? DataManager.ShadowEntry(saveEntry)
                        : null;
            }

            for (int i = 0; i < scope.Length; i++)
            {
                string s = scope[i];
                bool match = s.EndsWith(':')
                    ? saveEntry.StartsWith(s, StringComparison.Ordinal)   // whole category
                    : string.Equals(s, saveEntry, StringComparison.Ordinal);

                if (match) return DataManager.ShadowEntry(saveEntry);
            }

            return null;
        }

        // ===============================================================
        // Game hooks
        // ===============================================================

        private unsafe bool mio_has_Hook(On.MioGame.On_Mio.orig_has orig, Mio* __this, MioGame.String* item_id)
        {
            // Fast path. Mio::has is one of the hottest functions in the game and
            // most calls happen outside any scope, where the real entry is
            // authoritative - so skip marshalling the id entirely.
            if (_scopeEntries == null && !Diagnostics)
                return orig(__this, item_id);

            try
            {
                string id = Util.MioStringToString(*item_id);

#pragma warning disable CS0162
                if (Diagnostics) ReportIfUnscoped(orig, __this, id);
#pragma warning restore CS0162

                string? shadow = ShadowFor(id);
                if (shadow != null)
                {
                    MioGame.String s = Util.StringToMioString(shadow);
                    return orig(__this, &s);
                }
            }
            catch (Exception ex) { LogMessage("[hooks] has failed: " + ex); }

            return orig(__this, item_id);
        }

        private unsafe Save_entry* loot_1_Hook(
    On.MioGame.On_Game.orig_loot_1 orig,
    Game* __this, MioGame.String* item_id, int count, Loot_flags flags)
        {
            try
            {
                string id = Util.MioStringToString(*item_id);
                ref Game game = ref MioGame.Globals.game;

#pragma warning disable CS0162
                if (Diagnostics)
                    LogMessage($"[loot1] '{id}' scope={_scopeName ?? "-"}{ScanStackForGameFrames()}");
#pragma warning restore CS0162

                // Second half of a carcass pickup: a branch below already sent
                // the check, so drop the vanilla currency grant that follows.
                if (_suppressNextLoot != null && id == _suppressNextLoot)
                {
                    _suppressNextLoot = null;
                    MioGame.String e = Util.StringToMioString(id);
                    return game.save.peek(&e);
                }

                // Nacre pickups loot under a unique per-node entry ("CARCASS:0x..."),
                // which identifies the location exactly.
                var carcassLoc = dataManager.GetLocationByCarcassEntry(id);
                if (carcassLoc != null)
                {
                    apManager.SendLocationCheck(carcassLoc);
                    _suppressNextLoot = NacreEntry;

                    // Skipping orig is what suppresses the vanilla Nacre grant.
                    return AcquireWithCount(ref game, id);
                }

                // Liquid Nacre is an AP item but never a location's vanilla item -
                // it only appears as a world drop, so let the game grant it.
                if (dataManager.IsApItem(id) && id != LiquidNacreEntry)
                {
                    string room = Util.MioStringToString(game.current_zone_id);
                    Location? location = dataManager.ResolveLocation(id, room);

                    if (location != null)
                        apManager.SendLocationCheck(location);
                    else
                        LogMessage($"[AP] unresolved location: '{id}' in room '{room}' - no check sent");

                    // Old Core locations mark themselves consumed with their real
                    // CARCASS entry, which is what despawns the world pickup. The
                    // pickup also bumps FULL_PEARLS in a second loot call, which
                    // has to go too or the player banks a core AP never sent.
                    bool isOldCore = id.StartsWith(CarcassCategory + ":", StringComparison.Ordinal);
                    if (isOldCore) _suppressNextLoot = FullPearlsEntry;

                    string consumedEntry = isOldCore ? id : DataManager.ShadowEntry(id);

                    return AcquireWithCount(ref game, consumedEntry);
                }
            }
            catch (Exception ex) { LogMessage("[hooks] loot failed: " + ex); }

            return orig(__this, item_id, count, flags);
        }

        /// <summary>
        /// Suppresses the vanilla "you got X" popup for AP-managed pickups.
        ///
        /// FULL_PEARLS is suppressed too, despite not being an AP item itself:
        /// an Old Core pickup raises its popup separately from the loot call the
        /// loot hook drops, so without this the player is told they received a
        /// core they didn't get. Nothing legitimate is hidden - a core sent by
        /// Archipelago is written straight to the save and never reaches here.
        /// </summary>
        private unsafe void loot_popup_start_Hook(
            On.MioGame.On_Ui_loot_popup.orig_start orig, Ui_loot_popup* __this, MioGame.String* item_id)
        {
            try
            {
                string id = Util.MioStringToString(*item_id);

#pragma warning disable CS0162
                if (Diagnostics) LogMessage($"[popup] '{id}'");
#pragma warning restore CS0162

                if (id == FullPearlsEntry) return;
                if (dataManager.IsApItem(id) && id != LiquidNacreEntry) return;
            }
            catch (Exception ex) { LogMessage("[hooks] loot popup failed: " + ex); }

            orig(__this, item_id);
        }

        /// <summary>
        /// Trinket capacity derived from the upgrades the player actually holds,
        /// rather than from the vanilla accounting.
        /// </summary>
        private unsafe uint trinket_slots_count_Hook(
            On.MioGame.On_Mio.orig_trinket_slots_count orig, Mio* __this)
        {
            try
            {
                ref Game game = ref MioGame.Globals.game;
                int upgrades = HeldInCategory(ref game, TrinketSlotUpgradeCategory);
                return TrinketSlotsBase + (TrinketSlotsPerUpgrade * (uint)upgrades);
            }
            catch (Exception ex)
            {
                LogMessage("[hooks] trinket slots failed: " + ex);
                return orig(__this);
            }
        }

        /// <summary>
        /// Redirects save files to archipelago_slot_N.save, so an Archipelago run
        /// never overwrites the player's vanilla saves.
        /// </summary>
        private unsafe void stringfv_Hook(
            On.MioGame.GlobalFunctions.core.On_fmt.orig_stringfv orig,
            MioGame.String* __return, sbyte* fmt, Array_Val_ref* args)
        {
            // stringfv is the engine's general-purpose formatter and runs
            // constantly, so test the prefix against raw bytes before allocating
            // a managed string. Short-circuiting stops at the null terminator.
            if (fmt != null && StartsWithSlot(fmt))
            {
                try
                {
                    string f = new string(fmt);
                    if (f == "slot_%.save" || f == "slot_%_bck")
                    {
                        IntPtr buf = Marshal.StringToHGlobalAnsi("archipelago_" + f);
                        try { orig(__return, (sbyte*)buf, args); return; }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                catch (Exception ex) { LogMessage("[hooks] stringfv failed: " + ex); }
            }

            orig(__return, fmt, args);
        }

        private static unsafe bool StartsWithSlot(sbyte* p) =>
            p[0] == 's' && p[1] == 'l' && p[2] == 'o' && p[3] == 't' && p[4] == '_';

        /// <summary>
        /// Ejects the player from the glitch world anywhere outside the intro
        /// room. Reaching it elsewhere without the progression that normally
        /// precedes it leaves no way back out.
        /// </summary>
        private unsafe void Glitch_state_update_Prefix()
        {
            try
            {
                ref Game game = ref MioGame.Globals.game;

                // Cheap test first: this runs every frame and the zone id needs
                // marshalling out of a MioGame.String.
                if (!game.glitch.is_inside()) return;
                if (Util.MioStringToString(game.current_zone_id) == GlitchZone) return;

                game.exit_glitch();
            }
            catch (Exception ex) { LogMessage("[hooks] glitch exit failed: " + ex); }
        }

        /// <summary>
        /// Hints whatever Mel is currently offering. Reading the grid the game
        /// just built is how we learn which offers are visible without
        /// reimplementing the scrapling tier conditions.
        /// </summary>
        private unsafe void Update_item_grid_Suffix(Workshop_ui* __this, Node2* n)
        {
            try
            {
                if (!apManager.IsConnected) return;

                List<long>? fresh = null;
                ref var grid = ref __this->item_grid;
                Workshop_ui_item* items = (Workshop_ui_item*)grid.data.data;
                if (items == null) return;

                for (uint i = 0; i < grid.count; i++)
                {
                    var item = items[i].item;
                    if (item == null) continue;

                    string id = Util.MioStringToString(item->id.@ref);

                    Location? loc = dataManager.ResolveLocation(id, ShopRoom);
                    if (loc == null) continue;

                    if (!_hintedShopLocations.Add(loc.Id)) continue;
                    (fresh ??= new List<long>()).Add(loc.Id);
                }

                if (fresh == null) return;

                // Un-record on failure so a reconnect retries them.
                if (!apManager.SendHints(fresh))
                    foreach (long id in fresh) _hintedShopLocations.Remove((int)id);
            }
            catch (Exception ex) { LogMessage("[hooks] shop hint failed: " + ex); }
        }

        // ===============================================================
        // Diagnostics
        //
        // Finds Mio::has call sites that still need a scope. A managed hook
        // can't see its native caller, so the symbol table below turns raw
        // return addresses into game function names, and the stack is scanned
        // rather than unwound (unwinding can't cross the trampoline boundary).
        // All of this is inert unless Diagnostics is true.
        // ===============================================================

        [DllImport("kernel32.dll")]
        private static extern void GetCurrentThreadStackLimits(out IntPtr low, out IntPtr high);

        private static long[]? _fnAddrs;      // sorted ascending
        private static string[]? _fnNames;    // parallel to _fnAddrs

        private readonly HashSet<string> _unscopedReported = new(StringComparer.Ordinal);

        /// <summary>
        /// Callers confirmed to be asking "does the player OWN this", which must
        /// read the real entry. Not bugs; suppressed so new sites stand out.
        /// </summary>
        private static readonly HashSet<string> BenignCallers = new(StringComparer.Ordinal)
        {
            "MioGame.Achievements.evaluate",           // achievement polling on room entry
            "MioGame.Tab_trinkets.ui_update",          // inventory UI
            "MioGame.Amytis_encounter.skip_cutscene",  // misattributed Game::update_doors
        };

        /// <summary>
        /// Logs an unscoped Mio::has for an item whose location was already
        /// checked - the state in which an uncovered pickup would respawn.
        /// </summary>
        private unsafe void ReportIfUnscoped(On.MioGame.On_Mio.orig_has orig, Mio* __this, string id)
        {
            if (_scopeName != null || !dataManager.IsApItem(id)) return;

            string room = Util.MioStringToString(MioGame.Globals.game.current_zone_id);
            if (dataManager.ResolveLocation(id, room) == null) return;

            // Ask the game whether the shadow entry is set, i.e. whether this
            // location has already been checked.
            MioGame.String sh = Util.StringToMioString(DataManager.ShadowEntry(id));
            if (!orig(__this, &sh)) return;

            string caller = TopGameFrameName();
            if (BenignCallers.Contains(caller)) return;

            if (_unscopedReported.Add(room + "|" + id + "|" + caller))
                LogMessage($"[has-unscoped] '{id}' in '{room}' from {caller}{ScanStackForGameFrames()}");
        }

        /// <summary>
        /// Indexes every function address MioBinds exposes through its Pointers
        /// classes, so a raw address can be resolved to a name.
        /// </summary>
        private void BuildSymbolTable()
        {
            var list = new List<(long addr, string name)>(12000);

            foreach (System.Type t in typeof(Game).Assembly.GetTypes())
            {
                if (t.Name != "Pointers") continue;
                string owner = t.DeclaringType?.FullName ?? "?";

                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    try
                    {
                        long addr = f.GetValue(null) switch
                        {
                            IntPtr p => p.ToInt64(),
                            UIntPtr u => (long)u.ToUInt64(),
                            _ => 0,
                        };
                        if (addr != 0) list.Add((addr, owner + "." + f.Name));
                    }
                    catch { /* some function-pointer fields refuse GetValue */ }
                }
            }

            list.Sort((a, b) => a.addr.CompareTo(b.addr));
            _fnAddrs = new long[list.Count];
            _fnNames = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                _fnAddrs[i] = list[i].addr;
                _fnNames[i] = list[i].name;
            }

            LogMessage($"[sym] indexed {_fnAddrs.Length} function addresses");
        }

        /// <summary>Nearest known function at or below an address, plus the offset.</summary>
        private static string Resolve(long addr)
        {
            if (_fnAddrs == null || _fnAddrs.Length == 0) return $"0x{addr:X}";

            int i = Array.BinarySearch(_fnAddrs, addr);
            if (i < 0) i = ~i - 1;
            if (i < 0) return $"0x{addr:X}";

            long off = addr - _fnAddrs[i];

            // A very large offset means the real owner isn't in the table, so the
            // name would be the previous function and misleading.
            return off > 0x4000 ? $"0x{addr:X} <unknown>" : $"{_fnNames![i]}+0x{off:X}";
        }

        /// <summary>
        /// Finds game return addresses by scanning raw stack memory. Unwinding
        /// can't cross the managed/trampoline boundary, so this keeps any value
        /// that resolves to a known function at a plausible offset - the symbol
        /// table does the filtering. Expect some stale values; read the result
        /// as an unordered shortlist rather than a call sequence.
        /// </summary>
        private static unsafe string ScanStackForGameFrames(int maxHits = 10, int maxBytes = 8192)
        {
            if (_fnAddrs == null || _fnAddrs.Length == 0) return "\n      <no symbol table>";

            long lo = _fnAddrs[0];
            long hi = _fnAddrs[^1] + 0x10000;

            GetCurrentThreadStackLimits(out _, out IntPtr stackHigh);

            long probe = 0;
            long* sp = &probe;                       // roughly the current stack pointer
            long* end = (long*)stackHigh.ToInt64();  // the stack grows down, so this is outward
            long* cap = sp + (maxBytes / sizeof(long));
            if (cap < end) end = cap;

            var sb = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int hits = 0;

            for (long* p = sp; p < end && hits < maxHits; p++)
            {
                long v = *p;
                if (v < lo || v > hi) continue;

                int i = Array.BinarySearch(_fnAddrs, v);
                if (i < 0) i = ~i - 1;
                if (i < 0) continue;

                long off = v - _fnAddrs[i];

                // Offset 0 is a function pointer in a local, not a return address.
                if (off <= 0 || off > 0x4000) continue;

                string name = _fnNames![i];
                if (!seen.Add(name)) continue;

                sb.Append("\n      ").Append(name).Append("+0x").Append(off.ToString("X"));
                hits++;
            }

            return sb.Length == 0 ? "\n      <no game frames on stack>" : sb.ToString();
        }

        /// <summary>Innermost resolved frame, without its offset.</summary>
        private static string TopGameFrameName()
        {
            string s = ScanStackForGameFrames(maxHits: 1);
            int nl = s.LastIndexOf('\n');
            string frame = nl >= 0 ? s[(nl + 1)..].Trim() : "?";

            int plus = frame.IndexOf('+');
            return plus > 0 ? frame[..plus] : frame;
        }
    }
}