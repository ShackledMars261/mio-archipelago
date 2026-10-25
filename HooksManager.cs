using MioGame;
using MioModdingApi;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
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
    ///
    /// MEL'S SHOP is the one feature that reaches past the save layer into the
    /// UI, so that an offer shows what Archipelago will actually send. The list
    /// rows and the detail panel get there by different routes; see the shop
    /// section near the bottom for why.
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

        /// <summary>
        /// Report unscoped calls for this entry even when its location hasn't
        /// been checked. The normal filter requires the shadow to be set, which
        /// is no use when the bug is that the pickup can't be taken at all.
        /// Empty string disables it.
        /// </summary>
        private const string ProbeEntry = "";

        // ---------------------------------------------------------------
        // Save entries and categories
        // ---------------------------------------------------------------

        private const string NacreEntry = "RESOURCE:SOLID_DROPLETS";      // Crystallized Nacre
        private const string LiquidNacreEntry = "RESOURCE:PEARL_SHARDS";  // Liquid Nacre
        private const string FullPearlsEntry = "RESOURCE:FULL_PEARLS";    // unspent Old Cores
        private const string ExtraShieldsEntry = "RESOURCE:EXTRA_SHIELDS";

        private const string CarcassCategory = "CARCASS";
        private const string DialogCategory = "DIALOG";
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
        /// The count to write after acquire() when nothing says otherwise.
        ///
        /// Match vanilla: a completed save holds 65 of 74 entries at count 1,
        /// and the exceptions are either state markers the mod never grants
        /// (FLOOR_ELEVATOR) or the nacre balances, which GrantItem handles on
        /// its own. An entry's name doesn't tell us whether its readers test
        /// existence or count - Mio::has tests existence but
        /// Mio::has_more_than_zero does not - so writing what the game writes
        /// is the only safe default. Granting at 0 is what kept the crucible
        /// keys and the crucible reports from working.
        /// </summary>
        private const int DefaultGrantCount = 1;

        /// <summary>
        /// Entries whose count differs from DefaultGrantCount. A key may be a
        /// full entry ("ATTACK_POWER:1") or a category prefix ("ATTACK_POWER")
        /// covering every entry in it; a full entry wins over a category.
        ///
        /// Empty today - every category we grant wants 1. It stays as the place
        /// to record a genuine exception when one turns up.
        ///
        /// Counts are SET rather than incremented, so writing the same entry
        /// twice is idempotent.
        /// </summary>
        private static readonly Dictionary<string, int> GrantCounts = new(StringComparer.Ordinal);

        /// <summary>
        /// Extra entries to write alongside an Archipelago grant, keyed by the
        /// item's own save entry.
        ///
        /// Some pickups write more than one entry, and the mod only knows about
        /// the one the item maps to. Collecting a crucible report writes the
        /// KEY entry - the report itself - and, in the same instant, the DIALOG
        /// entry recording that its readout was shown. Writing both makes an
        /// Archipelago grant indistinguishable from a pickup.
        /// </summary>
        private static readonly Dictionary<string, string[]> CompanionEntries = new(StringComparer.Ordinal)
        {
            { "KEY:CRUCIBLE_GLOOMWATER", new[] { "DIALOG:TXT_CRUCIBLE_DATAREPORT_GLOOMWATER" } },
            { "KEY:CRUCIBLE_CRYSTAL",    new[] { "DIALOG:TXT_CRUCIBLE_DATAREPORT_CRYSTALLISATION" } },
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
        /// The bunker key. Its pickup is Halyn's terminal rather than a loot
        /// node, so none of the generic pickup scopes cover it - unscoped, a
        /// player who already holds the key from Archipelago can't interact
        /// with the terminal and the check can never be sent.
        /// </summary>
        private static readonly string[] ScopeBunkerKey = { BunkerKeyEntry };

        private const string BunkerKeyEntry = "KEY:BUNKER_KEY";

        /// <summary>
        /// Rooms whose bunker door has to track the pickup rather than the key.
        ///
        /// Three doors open on the bunker key. Two of them are the way out of
        /// the room the key is found in, so a key arriving early from
        /// Archipelago must not open them, and taking the pickup must. The
        /// third gates a large area and genuinely needs the real key.
        ///
        /// There is no per-door hook to scope: the lock is scripted rather
        /// than authored into Door_node.starts_locked_by_key, which reports no
        /// key for any of them. What does separate them is the room the query
        /// runs in - one door each, in two different wings:
        ///
        ///     ST_security_spider_P4   key room     -> DOOR:TO_SECURITY_ASC_P2_2
        ///     ST_security_spider_P5   hallway out  -> DOOR:TO_SECURITY_ASC_P2
        ///     GA_vin_transi_F3        the big door -> DOOR:door_ga_vin_bunker_2
        ///
        /// Only the first two are listed, so the third keeps the real entry.
        /// </summary>
        private static readonly string[] BunkerPickupRooms =
            { "ST_security_spider_P4", "ST_security_spider_P5" };

        /// <summary>
        /// True while the player is in one of those rooms. Refreshed once a
        /// frame, so Mio::has pays a bool test rather than a string compare.
        /// </summary>
        private bool _inBunkerPickupRoom;

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
        /// Flowing Steps. Its Samsk runs through update_hacker_crucible rather
        /// than the shared update_hacker, so ScopeHacker never reaches it.
        ///
        /// Deliberately narrower than ScopeHacker: this handler also tests the two
        /// crucible keys, and those are prerequisites the player must really own.
        /// Redirecting them to their shadows would block anyone who received a key
        /// from Archipelago without doing the challenge it came from.
        /// </summary>
        private static readonly string[] ScopeSpiderGoo = { "UNLOCK:SPIDER_GOO" };

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

        // ---------------------------------------------------------------
        // Mel's shop
        // ---------------------------------------------------------------

        /// <summary>The room containing Mel's shop.</summary>
        private const string ShopRoom = "HUB_hub_shop";

        /// <summary>Loca keys the detail panel looks up for the selected offer.</summary>
        private const string ItemNamePrefix = "ITEM_NAME_";
        private const string ItemDescriptionPrefix = "ITEM_DESCRIPTION_";
        private const string ItemFlavourPrefix = "ITEM_FLAVOUR_";

        /// <summary>True inside either of Mel's shop passes, update or draw.</summary>
        private static bool InShopScope =>
            _scopeName == "workshop_ui" || _scopeName == "workshop_render";

        /// <summary>
        /// Shop locations already hinted, so a grid rebuild doesn't re-send.
        /// Pairs with ArchipelagoManager.ClearScouts: both describe one
        /// multiworld and must be cleared together.
        /// </summary>
        private readonly HashSet<int> _hintedShopLocations = new();

        /// <summary>Mel's offers as location ids, for the connect-time scout.</summary>
        private readonly long[] _shopLocationIds;
        private bool _shopPrescouted;

        /// <summary>
        /// Label nodes collected during a grid rebuild, in grid order. The game
        /// sets each row's text while building, before item_grid is readable,
        /// so we note the nodes then and rewrite them in the suffix.
        /// </summary>
        private readonly List<IntPtr> _rowTextNodes = new();
        private bool _inGridBuild;

        /// <summary>
        /// Persistent MioGame.String instances handed back from try_translate,
        /// keyed by the loca key they answer. The engine treats that return as
        /// a pointer into the loca table - memory it never frees and expects to
        /// stay valid - so these are allocated once and kept for the session.
        /// </summary>
        private readonly Dictionary<string, IntPtr> _locaOverrides = new(StringComparer.Ordinal);

        /// <summary>
        /// The base ending (Ati) has been beaten. Recorded in the save rather than
        /// only sent, so a kill that lands while disconnected still reports on the
        /// next connect and a reload can't lose it. Shii's ending will want its own
        /// entry when it becomes a second victory condition.
        /// </summary>
        private const string VictoryEntry = "ARCHIPELAGO_META:VICTORY";

        private bool _victoryReported;

        /// <summary>
        /// Save entry -> the event name the browser map expects. The game writes
        /// all of these itself, so there are no per-event hooks: the pump watches
        /// the save and reports what it finds, which also picks up anything done
        /// while disconnected or before this feature shipped.
        ///
        /// Mel Kidnapped and Mel Freed are absent on purpose - they live in
        /// plotpoints.mel.quest rather than in a save entry, and are handled
        /// separately.
        /// </summary>
        private static readonly (string Entry, string Event)[] MapEvents =
        {
            ("DIALOG:TXT_SHII_FILLED_3",       "Shii Partially Awakened"),
            ("DIALOG:TXT_SHII_FILLING_DONE",   "Shii Fully Awakened"),

            ("BOSS:BLACKSMITH",                "Acat Defeated"),
            ("BOSS:TWIN_CRABASH",              "Ancile & Targa Defeated"),
            ("BOSS:PROJEKT",                   "AnRa & Dr. Halyn Defeated"),
            ("BOSS:SASUKE",                    "Ati Defeated"),
            ("BOSS:TRAPU",                     "Atmos Defeated"),
            ("BOSS:ZANGY",                     "Calderon Defeated"),
            ("BOSS:SCARECROW",                 "Crow Defeated"),
            ("BOSS:RAMBO",                     "Debby Defeated"),
            ("BOSS:CRABASH",                   "Egis Defeated"),
            ("BOSS:VINE",                      "Flora Defeated"),
            ("BOSS:SPIDY",                     "Friends :) Defeated"),
            ("BOSS:DEVOURER",                  "Lombre Defeated"),
            ("BOSS:BAMBY",                     "Nabuu Defeated"),
            ("BOSS:DOOR",                      "Poltergates Defeated"),
            ("BOSS:SAW_LONGUE",                "Sawlong Defeated"),
            ("BOSS:SHII",                      "Shii Defeated"),
            ("BOSS:TWIN",                      "Sol & Vin Defeated"),
            ("BOSS:WASHING_MACHINE",           "Vlad Defeated"),

            ("HEART_ATTACK:INTRO",             "Starting Tremor"),
            ("HEART_ATTACK:TUBE",              "Debby Tremor"),
            ("HEART_ATTACK:LATE_GAME",         "\"Other\" Tremor"),
            ("HEART_ATTACK:HORNFELL",          "Hornfell Tremor"),

            ("HUB_TUNER:LIHO",                 "Turn In Liho's Voice"),
            ("HUB_TUNER:HALYN",                "Turn In Halyn's Voice"),
            ("HUB_TUNER:ASMA",                 "Turn In Asma's Voice"),
            ("HUB_TUNER:AMYTIS",               "Turn In Amytis' Voice"),

            ("DIALOG:TXT_HUB_MEL_WAITING_ONCE", "Find Mel"),
            ("DIALOG:TXT_RAD_FREE",            "Find Rad"),
            ("DIALOG:TXT_SIN_EXIT",            "Find Sin"),
            ("DIALOG:TXT_COS_EXIT",            "Find Cos"),
            ("DIALOG:TXT_TAN_EXIT",            "Find Tan"),

            ("REBUILD_NPC:PUPPET_ALICE",       "Rescue Alice"),
            ("CODE:LQ_UDG_BIG_FAN",            "Foundations Big Fan"),
            ("CODE:ST_CUVE_01_ACTIVATED",      "Pump #1"),
            ("CODE:ST_CUVE_02_ACTIVATED",      "Pump #2"),
            ("CODE:ST_CUVE_03_ACTIVATED",      "Pump #3"),
            ("CODE:ST_CUVE_04_ACTIVATED",      "Pump #4"),
            ("CODE:WHEEL_FINGER_REPAIRED",     "Wheel Repaired"),
        };

        private const string MelKidnappedEvent = "Mel Kidnapped";
        private const string MelFreedEvent = "Mel Freed";

        /// <summary>Events already on the server, keyed by event name.</summary>
        private readonly HashSet<string> _pushedEvents = new(StringComparer.Ordinal);

        /// <summary>
        /// Frames between scans. Every frame would be 18 string marshals and save
        /// lookups for something that changes a handful of times in a whole run.
        /// </summary>
        private const int EventScanInterval = 60;
        private int _eventScanCountdown;

        /// <summary>Old Cores awarded by conversation rather than a world pickup.</summary>
        private static readonly HashSet<string> DialogOldCores = new(StringComparer.Ordinal)
        {
            "DIALOG:TXT_LQ_CITY_FOUNDATIONS",   // Poncia
            "DIALOG:TXT_GA_VIN_SUDDEN_PEARL",   // Harpagon
            "DIALOG:TXT_GA_BOU_MINDYOURSTEP",   // Ollantay
        };

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

            var ids = new List<long>(_scopeShopOffers.Length);
            foreach (string entry in _scopeShopOffers)
            {
                Location? loc = dataManager.ResolveLocation(entry, ShopRoom);
                if (loc != null) ids.Add(loc.Id);
            }
            _shopLocationIds = ids.ToArray();
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

            //On.MioGame.On_Bumper_node.activate.Prefix += BumperKnockbackLink;

            // Per-frame pump for queued Archipelago work.
            On.MioGame.On_Game.fixed_update.Prefix += fixed_update_Prefix;

            // Item and location interception.
            On.MioGame.On_Game.loot_1.Hook += loot_1_Hook;
            On.MioGame.On_Mio.has.Hook += mio_has_Hook;
            On.MioGame.On_Mio.has_more_than_zero.Hook += mio_has_more_than_zero_Hook;
            On.MioGame.On_Ui_loot_popup.start.Hook += loot_popup_start_Hook;
            On.MioGame.On_Mio.trinket_slots_count.Hook += trinket_slots_count_Hook;

            // Keeps an Archipelago run off the player's vanilla saves.
            On.MioGame.GlobalFunctions.core.On_fmt.stringfv.Hook += stringfv_Hook;

            // Skip Samsk tubes in order to prevent potential soft locks.
            On.MioGame.GlobalFunctions.game.On_game.glitch_state_update.Prefix += Glitch_state_update_Prefix;

            // Ati's defeat. update_death is the death state's per-frame update, so it
            // runs for the whole animation - MarkVictory is idempotent on its save
            // entry, which is what makes that harmless. The health check is the part
            // that matters: Boss_health drives phase changes through start_next_phase,
            // so rt.dead is only true once the fight is actually over.
            On.MioGame.On_Sasuke.update_death.Suffix +=
                (self, n) =>
                {
                    try
                    {
                        if (self->health.rt.dead) MarkVictory();
                    }
                    catch (Exception ex) { LogMessage("[hooks] victory hook failed: " + ex); }
                };

            InitShopHooks();
            InitScopeHooks();

            LogMessage("[hooks] installed");
        }

        /// <summary>
        /// Mel's shop. Three jobs: hint the visible offers, relabel the list
        /// rows, and relabel the detail panel. They need three different hooks
        /// because the UI produces those two pieces of text by different
        /// routes - see the shop section for the detail.
        /// </summary>
        private unsafe void InitShopHooks()
        {
            // Collect the row label nodes while the game builds the grid. It
            // sets each row's text before item_grid is readable, so the rewrite
            // has to wait for the suffix.
            On.MioGame.On_Workshop_ui.update_item_grid.Hook +=
                (orig, self, n) =>
                {
                    _rowTextNodes.Clear();
                    _inGridBuild = true;
                    try { orig(self, n); }
                    finally { _inGridBuild = false; }
                };

            On.MioGame.GlobalFunctions.game.On_ui.set_text_1.Hook += Shop_set_text_Hook;

            // Hints the visible offers and rewrites their labels.
            On.MioGame.On_Workshop_ui.update_item_grid.Suffix += Update_item_grid_Suffix;

            // Relabels the detail panel for whichever offer is highlighted.
            On.MioGame.On_Loca.try_translate.Hook += Shop_try_translate_Hook;
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

            // Flowing Steps' Samsk. Its own handler, so ScopeHacker never
            // reaches it.
            On.MioGame.On_Npc_node.update_hacker_crucible.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeSpiderGoo, "hacker_crucible"); orig(self, node); };

            // Tomo runs the crucible sequence and hands over Flowing Steps at
            // the end of it. He has his own handlers too.
            On.MioGame.On_Npc_node.update_tomo.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeSpiderGoo, "tomo"); orig(self, node); };

            On.MioGame.On_Npc_node.update_tomo2.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeSpiderGoo, "tomo2"); orig(self, node); };

            // Tomo's placement. Npc_manager::update_tomo_plotpoints decides it
            // by asking whether the player has Flowing Steps, and moves him home
            // once they do - which removes him from the crucible before an
            // Archipelago player can run the sequence. That function is INLINED
            // into update_npcs, so hooking the standalone symbol does nothing;
            // the inlined copy also picks TOMO_HOME straight from the has()
            // result, before it ever reads plotpoints.tomo.quest.
            //
            // Narrow on purpose: update_npcs also queries CODE:MEL_PLUGGED and
            // VOICE:HALYN, and those must keep reading the real save.
            On.MioGame.On_Game.On_Npc_manager.update_npcs.Hook +=
                static (orig, self) => { using var s = new Scope(ScopeSpiderGoo, "update_npcs"); orig(self); };

            On.MioGame.On_Hub_general.update_tuner3.Hook +=
                static (orig, self, node) => { using var s = new Scope(ScopeMap, "Hub_general_tuner3"); orig(self, node); };

            On.MioGame.On_Halyn_assistant_terminal.update_all.Hook +=
                static (orig, w) => { using var s = new Scope(ScopeBunkerKey, "halyn_terminal"); orig(w); };

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

            // The draw pass. Scoped for the same reason as the update pass, and
            // so the detail panel's loca lookups are recognised as the shop's
            // wherever they happen to originate.
            On.MioGame.On_Workshop_ui.render_all.Hook +=
                (orig, vp, w) => { using var s = new Scope(_scopeShopOffers, "workshop_render"); orig(vp, w); };
        }

        // ===============================================================
        // Save helpers
        // ===============================================================

        /// <summary>
        /// Count to write for a save entry after acquiring it. Full entry first,
        /// then category prefix, then DefaultGrantCount.
        /// </summary>
        private static int CountFor(string saveEntry)
        {
            if (GrantCounts.TryGetValue(saveEntry, out int exact)) return exact;

            int colon = saveEntry.IndexOf(':');
            if (colon > 0 && GrantCounts.TryGetValue(saveEntry[..colon], out int byCategory))
                return byCategory;

            return DefaultGrantCount;
        }

        /// <summary>
        /// Acquires a save entry and applies its configured count. Returns the
        /// entry so loot hooks can hand it straight back.
        /// </summary>
        private static unsafe Save_entry* AcquireWithCount(ref Game game, string saveEntry)
        {
            MioGame.String s = Util.StringToMioString(saveEntry);
            Save_entry* entry = game.save.acquire(&s);

            if (entry != null) entry->count = CountFor(saveEntry);

            return entry;
        }

        /// <summary>
        /// Writes the extra entries a pickup would have written alongside this
        /// one. See CompanionEntries.
        /// </summary>
        private static unsafe void GrantCompanions(ref Game game, string saveEntry)
        {
            if (!CompanionEntries.TryGetValue(saveEntry, out string[]? companions)) return;

            foreach (string companion in companions)
                AcquireWithCount(ref game, companion);
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
            ProcessOtherArchipelago(__this);

            try
            {
                // Ahead of the drain, which bails out at the menu - this has to
                // keep up with the room either way or it latches on stale state.
                string room = Util.MioStringToString(MioGame.Globals.game.current_zone_id);
                _inBunkerPickupRoom = Array.IndexOf(BunkerPickupRooms, room) >= 0;
            }
            catch (Exception ex) { LogMessage("[hooks] room refresh failed: " + ex); }

            try
            {
                // The queues keep filling while we wait, so nothing is lost.
                if (!IsSaveReady()) return;
                apManager.DrainPending(GrantItem, MarkLocationChecked);
            }
            catch (Exception ex) { LogMessage("[hooks] drain failed: " + ex); }

            try
            {
                if (!apManager.IsConnected)
                {
                    if (_shopPrescouted)
                    {
                        // The session ended. Everything we know about Mel's
                        // offers described that multiworld, so forget it -
                        // a reconnect elsewhere must not show its items.
                        _shopPrescouted = false;
                        _hintedShopLocations.Clear();
                        _locaOverrides.Clear();
                        apManager.ClearScouts();
                        apManager.ClearGoalSent();
                    }
                }
                else if (!_shopPrescouted && _shopLocationIds.Length > 0)
                {
                    _shopPrescouted = apManager.ScoutLocations(_shopLocationIds, asHint: false);
                }
            }
            catch (Exception ex) { LogMessage("[hooks] shop pre-scout failed: " + ex); }

            try
            {
                if (!_victoryReported && apManager.IsConnected)
                {
                    ref Game game = ref MioGame.Globals.game;
                    MioGame.String v = Util.StringToMioString(VictoryEntry);

                    if (game.save.acquired_count(&v) > 0)
                        _victoryReported = apManager.SendGoalCompletion();
                }
            }
            catch (Exception ex) { LogMessage("[hooks] goal report failed: " + ex); }

            try
            {
                if (apManager.IsConnected
                    && _pushedEvents.Count < MapEvents.Length + 2   // +2: the Mel pair
                    && --_eventScanCountdown <= 0)
                {
                    _eventScanCountdown = EventScanInterval;

                    ref Game game = ref MioGame.Globals.game;

                    foreach ((string entry, string name) in MapEvents)
                    {
                        if (_pushedEvents.Contains(name)) continue;

                        MioGame.String e = Util.StringToMioString(entry);
                        if (game.save.acquired_count(&e) > 0) PushEvent(name);
                    }

                    // Mel's capture and rescue are quest states, not save entries.
                    var quest = game.save.plotpoints.mel.quest;
                    if (quest >= Plotpoints.Mel.Quest.Captured) PushEvent(MelKidnappedEvent);
                    if (quest >= Plotpoints.Mel.Quest.Returned) PushEvent(MelFreedEvent);
                }
            }
            catch (Exception ex) { LogMessage("[hooks] event scan failed: " + ex); }
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
                GrantCompanions(ref game, item.SaveEntry);
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
            string entry = MarksItselfConsumed(vanillaItem.SaveEntry)
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
            // Room-conditional rather than scope-driven, and so ahead of the
            // scope lookup: there is no caller to hook. See BunkerPickupRooms.
            if (_inBunkerPickupRoom && saveEntry == BunkerKeyEntry)
                return DataManager.ShadowEntry(saveEntry);

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
            if (_scopeEntries == null && !_inBunkerPickupRoom && !Diagnostics)
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

        /// <summary>
        /// The count-based sibling of Mio::has. Same scope rules - a call site
        /// asking "has this pickup been taken" means the same thing whichever
        /// function it asks through.
        /// </summary>
        private unsafe bool mio_has_more_than_zero_Hook(
            On.MioGame.On_Mio.orig_has_more_than_zero orig, Mio* __this, MioGame.String* item_id)
        {
            if (_scopeEntries == null && !_inBunkerPickupRoom) return orig(__this, item_id);

            try
            {
                string id = Util.MioStringToString(*item_id);

                string? shadow = ShadowFor(id);
                if (shadow != null)
                {
                    MioGame.String s = Util.StringToMioString(shadow);
                    return orig(__this, &s);
                }
            }
            catch (Exception ex) { LogMessage("[hooks] has_more_than_zero failed: " + ex); }

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

                    // An Old Core pickup also bumps FULL_PEARLS in a second loot
                    // call, which has to go too or the player banks a core AP
                    // never sent. Three of them are conversation rewards rather
                    // than world pickups, so the CARCASS prefix doesn't catch
                    // them and they're listed by name.
                    if (id.StartsWith(CarcassCategory + ":", StringComparison.Ordinal)
                        || DialogOldCores.Contains(id))
                        _suppressNextLoot = FullPearlsEntry;

                    string consumedEntry = MarksItselfConsumed(id) ? id : DataManager.ShadowEntry(id);

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

        // ===============================================================
        // Mel's shop
        //
        // Two pieces of text, reached two different ways.
        //
        // THE LIST ROWS carry nothing that identifies them. The label passes
        // through Loca as already-resolved text, several offers share one label
        // ("Coating Component" twice over), and the order the UI resolves them
        // in bears no relation to the order it draws them. What does work is
        // ui::set_text: the game sets each row's text once while building the
        // grid, in grid order, so noting those nodes during the build and
        // rewriting them afterwards gives exact identity by index.
        //
        // THE DETAIL PANEL is easy by comparison - it looks up
        // ITEM_NAME_<save entry>, which names the offer outright, so that one
        // is answered directly and is correct whichever row is highlighted.
        // ===============================================================

        /// <summary>
        /// Notes the label node of each row while the grid is being built.
        ///
        /// Each row sets three strings: name, cost, quantity. The costs and
        /// counts are numeric, and the one raw ITEM_NAME_ key belongs to the
        /// category header rather than a row, so skipping both leaves exactly
        /// the row labels, in grid order.
        /// </summary>
        private unsafe void Shop_set_text_Hook(
            On.MioGame.GlobalFunctions.game.On_ui.orig_set_text_1 orig,
            Node2* node, MioGame.String txt)
        {
            if (_inGridBuild)
            {
                try
                {
                    string s = Util.MioStringToString(txt);

                    if (!IsNumeric(s) && !s.StartsWith(ItemNamePrefix, StringComparison.Ordinal))
                        _rowTextNodes.Add((IntPtr)node);
                }
                catch (Exception ex) { LogMessage("[hooks] shop row capture failed: " + ex); }
            }

            orig(node, txt);
        }

        /// <summary>
        /// Hints whatever Mel is currently offering and relabels the rows.
        /// Reading the grid the game just built is how we learn which offers
        /// are visible without reimplementing the scrapling tier conditions.
        /// </summary>
        private unsafe void Update_item_grid_Suffix(Workshop_ui* __this, Node2* n)
        {
            try
            {
                List<long>? fresh = null;
                ref var grid = ref __this->item_grid;
                Workshop_ui_item* items = (Workshop_ui_item*)grid.data.data;
                if (items == null) return;

                var tiles = new int[grid.count];

                for (uint i = 0; i < grid.count; i++)
                {
                    tiles[i] = -1;

                    var item = items[i].item;
                    if (item == null) continue;

                    string id = Util.MioStringToString(item->id.@ref);

                    Location? loc = dataManager.ResolveLocation(id, ShopRoom);
                    if (loc == null) continue;

                    tiles[i] = loc.Id;

                    if (!apManager.IsConnected) continue;
                    if (!_hintedShopLocations.Add(loc.Id)) continue;
                    (fresh ??= new List<long>()).Add(loc.Id);
                }

                // Mismatched counts mean the row filter let something through
                // or dropped a label, which would shift every row by one.
#pragma warning disable CS0162
                if (Diagnostics)
                    LogMessage($"[shop] {grid.count} grid entries, {_rowTextNodes.Count} label nodes");
#pragma warning restore CS0162

                int rows = Math.Min(tiles.Length, _rowTextNodes.Count);
                for (int i = 0; i < rows; i++)
                {
                    if (tiles[i] < 0) continue;

                    // Null means the scout hasn't come back; the vanilla label
                    // stands until the next rebuild.
                    string? apName = apManager.ScoutedName(tiles[i]);
                    if (apName == null) continue;

                    MioGame.String s = Util.StringToMioString(apName);
                    MioGame.GlobalFunctions.game.ui.set_text((Node2*)_rowTextNodes[i], s);
                }

                if (fresh == null) return;

                // Un-record on failure so a reconnect retries them.
                if (!apManager.ScoutLocations(fresh, asHint: true))
                    foreach (long id in fresh) _hintedShopLocations.Remove((int)id);
            }
            catch (Exception ex) { LogMessage("[hooks] shop grid failed: " + ex); }
        }

        /// <summary>
        /// Answers the detail panel's ITEM_NAME_/ITEM_DESCRIPTION_/ITEM_FLAVOUR_
        /// lookups for Mel's offers. Flavour is blanked, since the vanilla quip
        /// describes an item the player isn't getting.
        /// </summary>
        private unsafe MioGame.String* Shop_try_translate_Hook(
            On.MioGame.On_Loca.orig_try_translate orig, Loca* self, MioGame.String* id)
        {
            if (!InShopScope) return orig(self, id);

            try
            {
                string key = Util.MioStringToString(*id);

                string? prefix =
                    key.StartsWith(ItemNamePrefix, StringComparison.Ordinal) ? ItemNamePrefix :
                    key.StartsWith(ItemDescriptionPrefix, StringComparison.Ordinal) ? ItemDescriptionPrefix :
                    key.StartsWith(ItemFlavourPrefix, StringComparison.Ordinal) ? ItemFlavourPrefix :
                    null;

                if (prefix != null)
                {
                    Location? loc = dataManager.ResolveLocation(key[prefix.Length..], ShopRoom);

                    // Only override once the scout is back - vanilla text beats
                    // a blank panel.
                    if (loc != null && apManager.ScoutedName(loc.Id) != null)
                    {
                        string text =
                            ReferenceEquals(prefix, ItemNamePrefix) ? apManager.ScoutedName(loc.Id)! :
                            ReferenceEquals(prefix, ItemDescriptionPrefix) ? apManager.ScoutedDescription(loc.Id) ?? "" :
                            "";

                        return CachedLocaString(key, text);
                    }
                }
            }
            catch (Exception ex) { LogMessage("[hooks] panel label failed: " + ex); }

            return orig(self, id);
        }

        /// <summary>Engine string for an overridden loca key, allocated once.</summary>
        private unsafe MioGame.String* CachedLocaString(string locaKey, string text)
        {
            if (_locaOverrides.TryGetValue(locaKey, out IntPtr existing))
                return (MioGame.String*)existing;

            IntPtr block = Marshal.AllocHGlobal(sizeof(MioGame.String));
            *(MioGame.String*)block = Util.StringToMioString(text);
            _locaOverrides[locaKey] = block;

            return (MioGame.String*)block;
        }

        private static bool IsNumeric(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            return true;
        }

        /// <summary>Reports an event once. Repeat calls are free.</summary>
        private void PushEvent(string name)
        {
            if (_pushedEvents.Contains(name)) return;

            if (apManager.AddToList(ArchipelagoManager.EventsKey, name,
                                    ArchipelagoManager.UpdateEventsBounce))
                _pushedEvents.Add(name);
        }

        /// <summary>
        /// True when an entry is the game's own "this is done" marker, so the real
        /// entry is written on a check rather than a shadow.
        ///
        /// A CARCASS entry is what despawns the world pickup. A DIALOG entry is what
        /// stops an NPC repeating a conversation - it's read back through
        /// game::npc::has_dialog, which is just has("DIALOG:" + id) and sits outside
        /// the scope system entirely, so a shadow would be invisible to it. Neither
        /// entry grants the player anything on its own, which is what makes writing
        /// the real one safe.
        /// </summary>
        private static bool MarksItselfConsumed(string saveEntry) =>
            saveEntry.StartsWith(CarcassCategory + ":", StringComparison.Ordinal) ||
            saveEntry.StartsWith(DialogCategory + ":", StringComparison.Ordinal);

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
            "MioGame.Amytis_encounter.skip_cutscene",  // misattributed
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
            // location has already been checked - the state an uncovered pickup
            // would respawn in. ProbeEntry skips that filter and reports
            // regardless, for the opposite bug: a pickup that never appears.
            if (!string.Equals(id, ProbeEntry, StringComparison.Ordinal))
            {
                MioGame.String sh = Util.StringToMioString(DataManager.ShadowEntry(id));
                if (!orig(__this, &sh)) return;
            }

            string caller = TopGameFrameName();
            if (BenignCallers.Contains(caller)) return;

            if (_unscopedReported.Add(room + "|" + id + "|" + caller))
                LogMessage($"[has-unscoped] '{id}' in '{room}' from {caller}{ScanStackForGameFrames()}");
        }

        /// <summary>Records the base ending as beaten and reports it to the server.</summary>
        private unsafe void MarkVictory()
        {
            ref Game game = ref MioGame.Globals.game;

            MioGame.String v = Util.StringToMioString(VictoryEntry);
            if (game.save.acquired_count(&v) > 0) return;   // already recorded

            AcquireWithCount(ref game, VictoryEntry);
            LogMessage("[AP] victory recorded (Ati)");

            _victoryReported = apManager.SendGoalCompletion();
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


        private bool alreadyDead = false;
        private int previousShields = -1;
        private Vec_float_3 zeroVector = new Vec_float_3() { Base = new _vec_storage_float_3() { x = 0.0f, y = 0.0f, z = 0.0f } };
        public static bool queuedRespawn;
        private int staggerFrames = 3;
        private int startDelay = 20;
        private bool satAtCheckpoint = false;
        private bool finishedIntro = false;


        private unsafe void ProcessOtherArchipelago(Game* __this)
        {
            if (startDelay > 0)
            {
                startDelay--;
                return;
            }
            
            ref var mio = ref __this->mio;
            ref var save = ref __this->save;

            if (mio.cutscene.active) return;
            if (!finishedIntro)
            {
                bool value = InStartingCutscene(ref mio);
                if (value)
                {
                    return;
                }
                else
                {
                    finishedIntro = true;
                }
            }
            

            if (mio.checkpoint.state._value != Mio.Checkpoint.State.None)
            {
                satAtCheckpoint = true;
                return;
            }
            if (!satAtCheckpoint)
            {
                if (Util.MioStringToString(save.checkpoint_id) != "cp_FOCUS_dispatch_zone")
                {
                    satAtCheckpoint = true;
                }
            }

            if (apManager.queuedWarp)
            {
                save.halyn_align.configuration_discrete = 2;
                mio.move_to(apManager.startingPositon);
                apManager.queuedWarp = false;
            }
            else if (!satAtCheckpoint)
            {
                if (mio.life.dead._value)
                {
                    queuedRespawn = true;
                }
                else if (queuedRespawn)
                {
                    if (staggerFrames > 0)
                    {
                        staggerFrames--;
                    }
                    else
                    {
                        queuedRespawn = false;
                        staggerFrames = 3;
                        mio.move_to(apManager.startingPositon);
                    }
                }
            }

            if (apManager.isDeathLink)
            {
                if (apManager.queuedDeathLink)
                {
                    //MioGame.GlobalFunctions.game.mio.mio_set_dead((Mio*)Unsafe.AsPointer(ref mio), null);
                    mio.life.health_points = 0;
                    mio.life.shield_points = 0;
                    Vec_float_3 pos = new();
                    pos.x = -967; pos.y = 1430; pos.z = 0;
                    mio.move_to(pos);
                    apManager.queuedDeathLink = false;
                }
                else if (mio.life.dead._value)
                {
                    if (!alreadyDead)
                    {
                        apManager.SendDeathLink();
                        alreadyDead = true;
                        LogMessage("[AP] Sending DeathLink!");
                    }
                }
                else
                {
                    alreadyDead = false;
                }
            }
            if (apManager.isKnockbackLink)
            {
                if (!ArchipelagoManager.IsVecFloat3Zero(apManager.queuedKnockbackLink))
                {
                    LogMessage(apManager.queuedKnockbackLink.y.ToString());
                    mio.velocity = ArchipelagoManager.AddVecFloat3(mio.velocity, ArchipelagoManager.MultiplyVecFloat3(apManager.queuedKnockbackLink, 2.0f));
                    mio.move_by(new Vec_float_3() { Base = new _vec_storage_float_3() { y = 1.0f } });
                    LogMessage(mio.velocity.y.ToString());
                    apManager.queuedKnockbackLink = zeroVector;
                }
                // Send knockbacklink here
            }
            if (apManager.isDamageLink)
            {
                if (apManager.damageLinkPoints >= ArchipelagoManager.DamageLinkThreshold && mio.life.shield_points > 0)
                {
                    int shieldsToLose = apManager.damageLinkPoints / ArchipelagoManager.DamageLinkThreshold;
                    shieldsToLose = Math.Min(mio.life.shield_points, shieldsToLose);
                    mio.life.shield_points -= shieldsToLose;
                    apManager.damageLinkPoints -= ArchipelagoManager.DamageLinkThreshold * shieldsToLose;
                }
                else if (previousShields > mio.life.shield_points && previousShields != -1)
                {
                    apManager.SendDamageLink(previousShields - mio.life.shield_points);
                    LogMessage("[AP] Sending DamageLink!");
                }
                previousShields = mio.life.shield_points;
            }
        }

        private static unsafe bool InStartingCutscene(ref Mio mio)
        {
            ref var node = ref Unsafe.AsRef<Node2>(mio.node);
            return node._transform.translation.y > 3000.0;
        }


        private unsafe void BumperKnockbackLink(Node2* n, Bumper_node* bn)
        {
            ref Bumper_node bumper = ref Unsafe.AsRef<Bumper_node>(bn);
            if (apManager.isKnockbackLink)
            {
                LogMessage($"Bumper activated with strength {bumper.strength} and range {bumper.range}");
            }
        }
    }
}