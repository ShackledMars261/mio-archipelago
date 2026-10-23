using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Converters;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Packets;
using MioGame.std;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using ItemSendMessage = Archipelago.MultiClient.Net.MessageLog.Messages.ItemSendLogMessage;

namespace MioAP
{
    /// <summary>Where the Archipelago session currently stands.</summary>
    internal enum ApState
    {
        Disconnected,
        Connecting,
        Connected,
        Failed,
    }

    /// <summary>What a scouted location holds, as the shop presents it.</summary>
    internal readonly record struct ScoutedOffer(string Name, string Description);

    /// <summary>
    /// Owns the Archipelago session and the boundary between its threads and the
    /// game's.
    ///
    /// THREADING. Every network callback arrives on the websocket thread and does
    /// exactly one thing: enqueue. Nothing touches the game's save from there. The
    /// game thread drains those queues once per fixed_update, which is the only
    /// place save writes happen, so no lock is ever held across native code.
    /// Connecting also runs on its own thread, so neither the GUI nor the save
    /// gate stalls on a bad host or a slow handshake.
    ///
    /// ITEM IDENTITY. Grants are keyed on an item's position in the received
    /// stream, not its item id. Filler items (both nacres) share a single id and
    /// can arrive any number of times, so the id can't identify a grant. The
    /// stream is ordered and append-only, which makes "applied through index N"
    /// sufficient - HooksManager stores that watermark in the save, so grants stay
    /// exactly-once across reloads.
    ///
    /// RECONNECTING. A reconnect re-enqueues the entire item history and every
    /// checked location rather than trying to resume. That's deliberate: replaying
    /// is cheap, and the watermark plus the idempotent shadow writes make it safe.
    /// Location checks are deduplicated locally and, if we aren't connected yet,
    /// deferred rather than dropped.
    ///
    /// STATE. SaveGate holds the menu-to-game transition until a connection
    /// resolves, so it needs to tell "still trying" from "failed" - hence a state
    /// enum rather than a bool. CurrentDetails exposes what actually worked, so
    /// the gate can remember it against the save slot.
    ///
    /// NOTIFYING THE PLAYER. A successful connection is announced from here, so
    /// it reads the same whoever started it - the menu or the save gate. The GUI
    /// isn't constructed yet when this is, so the toast sink is handed over
    /// afterwards rather than taken in the constructor.
    ///
    /// SCOUTING. Mel's shop shows what each offer really sends, which means
    /// knowing before the player buys. Scouts answer that: privately at connect
    /// time so the labels are ready, and again as a public hint when the player
    /// first sees an offer, so trackers pick it up.
    /// </summary>
    internal class ArchipelagoManager
    {
        private readonly Action<string> _loggingCBMethod;
        private readonly DataManager dataManager;

        private ArchipelagoSession? _session;

        private volatile ApState _state = ApState.Disconnected;
        private volatile string _lastError = "";
        private volatile ConnectionDetails? _currentDetails;

        /// <summary>An item from AP, tagged with its position in the received stream.</summary>
        private readonly record struct PendingItem(int Index, long ItemId, string Sender);

        private readonly ConcurrentQueue<PendingItem> _pendingItems = new();

        /// <summary>
        /// How far into AllItemsReceived we've enqueued. Network thread only. Reset on
        /// reconnect so everything is re-enqueued; the game-thread watermark dedups.
        /// </summary>
        private int _enqueuedThrough;

        // Location ids AP says are already checked, awaiting a shadow write.
        private readonly ConcurrentQueue<long> _pendingCheckedLocations = new();

        // Location checks we tried to send while disconnected.
        private readonly ConcurrentQueue<long> _deferredChecks = new();

        // Locations we've already sent this session, to avoid spamming.
        private readonly HashSet<long> _sentChecks = new();
        private readonly object _sentLock = new();

        public ApState State => _state;
        public bool IsConnected => _state == ApState.Connected;

        /// <summary>Why the last connection attempt failed; empty if none has.</summary>
        public string LastError => _lastError;

        /// <summary>
        /// The details of the live connection, for SaveGate to store against the
        /// save slot. Null until a connection has succeeded.
        /// </summary>
        public ConnectionDetails? CurrentDetails => _currentDetails;

        // Set after construction; see SetToastSink.
        private Action<string>? _showToast;

        public ArchipelagoManager(Action<string> loggingCBMethod, DataManager dataManager)
        {
            _loggingCBMethod = loggingCBMethod;
            this.dataManager = dataManager;
        }

        /// <summary>
        /// Supplies the on-screen notification sink. Separate from the constructor
        /// because the GUI is built after this manager; until it's called,
        /// notifications are simply dropped.
        /// </summary>
        public void SetToastSink(Action<string> showToast) => _showToast = showToast;

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        /// <summary>
        /// Notifies the player. Called from the connect thread, so it must not
        /// throw back into it - GuiManager only enqueues, but the sink is
        /// swappable, so guard it anyway.
        /// </summary>
        private void Toast(string message)
        {
            try { _showToast?.Invoke(message); }
            catch (Exception ex) { LogMessage("[AP] toast failed: " + ex); }
        }

        /// <summary>
        /// Non-blocking. Starts the connection on a background thread so the caller
        /// - the GUI on the render thread, or the save gate mid-transition - never
        /// stalls on network I/O. Ignored if a connection is already up or underway.
        /// </summary>
        public void Connect(string hostname, int port, string username, string password)
        {
            if (_state == ApState.Connecting || _state == ApState.Connected)
            {
                LogMessage($"[AP] ignoring connect request; already {_state}");
                return;
            }

            _state = ApState.Connecting;
            _lastError = "";

            var t = new Thread(() => ConnectBlocking(hostname, port, username, password))
            {
                Name = "MioAP-Connect",
                IsBackground = true,
            };
            t.Start();
        }

        private void ConnectBlocking(string hostname, int port, string username, string password)
        {
            try
            {
                LogMessage($"Connecting to {hostname}:{port} as \"{username}\"...");

                var session = ArchipelagoSessionFactory.CreateSession(hostname, port);
                session.MessageLog.OnMessageReceived += OnMessageReceived;
                session.Items.ItemReceived += ItemReceived;

                LoginResult result;
                try
                {
                    result = session.TryConnectAndLogin(
                        "Memories in Orbit", username,
                        ItemsHandlingFlags.AllItems, password: password);
                }
                catch (Exception e)
                {
                    result = new LoginFailure(e.GetBaseException().Message);
                }

                if (!result.Successful)
                {
                    var failure = (LoginFailure)result;

                    // Keep a short reason for the UI, and log the full detail.
                    string reason = failure.Errors.Length > 0
                        ? failure.Errors[0]
                        : failure.ErrorCodes.Length > 0
                            ? failure.ErrorCodes[0].ToString()
                            : "unknown error";

                    string msg = $"Failed to connect to {hostname}:{port} as {username}:";
                    foreach (string error in failure.Errors) msg += $"\n    {error}";
                    foreach (ConnectionRefusedError error in failure.ErrorCodes) msg += $"\n    {error}";
                    LogMessage(msg);

                    Fail(reason);
                    return;
                }

                _session = session;
                _currentDetails = new ConnectionDetails
                {
                    Host = hostname,
                    Port = port,
                    Slot = username,
                    Password = password,
                };
                _state = ApState.Connected;

                var success = (LoginSuccessful)result;
                LogMessage($"Connected to {hostname}:{port} as \"{username}\". (Slot #{success.Slot})");
                Toast($"Connected to Archipelago as {username}");

                _ = CacheSlotData();

                ResyncFromServer();
            }
            catch (Exception ex)
            {
                LogMessage("Connect failed: " + ex);
                Fail(ex.GetBaseException().Message);
            }
        }

        private void Fail(string reason)
        {
            _lastError = reason;
            _currentDetails = null;
            _session = null;
            _state = ApState.Failed;
        }

        private void OnMessageReceived(Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage message)
        {
            LogMessage($"[AP] {message}");

            // "Sent" is the server's own event rather than something we infer
            // from our checks, so it is right whichever way the location was
            // found. Items for ourselves are skipped - GrantItem already
            // toasts those as "Found X" when they come back to us.
            if (message is ItemSendMessage send
                && send.IsSenderTheActivePlayer
                && !send.IsReceiverTheActivePlayer)
            {
                Toast($"Sent {send.Item.ItemDisplayName} to {send.Receiver.Alias}");
            }
        }

        private void ResyncFromServer()
        {
            if (_session == null) return;

            isDeathLink = true; //GetSlotDataValue("death_link", false);
            UpdateTag("DeathLink", isDeathLink);
            deathLinkAmnesty = GetSlotDataValue("death_link_amnesty", 1);
            deathLinkGrace = GetSlotDataValue("death_link_grace", 1);
            deathLinkService = _session.CreateDeathLinkService();
            deathLinkService.OnDeathLinkReceived += ReceiveDeathLink;

            isKnockbackLink = true; //GetSlotDataValue("knockback_link", false);
            UpdateTag("KnockbackLink", isKnockbackLink);

            isDamageLink = true; //GetSlotDataValue("damage_link", false);
            UpdateTag("SharedDamage", isDamageLink);

            _session.Socket.PacketReceived += ReceiveBounce;

            // Drop anything queued from a previous session, then re-enqueue everything.
            // Indices are stable, so the save watermark skips whatever was already applied.
            while (_pendingItems.TryDequeue(out _)) { }
            _enqueuedThrough = 0;
            SyncReceivedItems();

            int locs = 0;
            foreach (long locId in _session.Locations.AllLocationsChecked)
            {
                _pendingCheckedLocations.Enqueue(locId);
                lock (_sentLock) _sentChecks.Add(locId);
                locs++;
            }

            LogMessage($"Resync queued: {_enqueuedThrough} items, {locs} checked locations.");

            while (_deferredChecks.TryDequeue(out long pending))
                SendLocationCheck(pending);
        }

        /// <summary>
        /// Enqueues every received item we haven't queued yet, tagged with its index.
        /// Network thread only - AllItemsReceived is mutated by the session.
        /// </summary>
        private void SyncReceivedItems()
        {
            if (_session == null) return;

            var all = _session.Items.AllItemsReceived;
            int mySlot = _session.ConnectionInfo.Slot;

            for (int i = _enqueuedThrough; i < all.Count; i++)
            {
                var info = all[i];

                // Empty means we found it ourselves; the caller words that differently.
                string sender = info.Player.Slot == mySlot ? "" : info.Player.Alias;
                _pendingItems.Enqueue(new PendingItem(i, info.ItemId, sender));
            }

            _enqueuedThrough = all.Count;
        }

        private void ItemReceived(Archipelago.MultiClient.Net.Helpers.ReceivedItemsHelper helper)
        {
            try
            {
                helper.DequeueItem();     // clear the helper's own queue
                SyncReceivedItems();      // index comes from AllItemsReceived
            }
            catch (Exception ex) { LogMessage("[AP] ItemReceived failed: " + ex); }
        }

        // ---- called from the game thread ----

        /// <summary>
        /// Drains queued work on the game thread. grantItem receives the item's index
        /// in the AP received stream; the caller is responsible for skipping indices it
        /// has already applied.
        /// </summary>
        public void DrainPending(Action<int, Item, string> grantItem, Action<Location> markLocationChecked)
        {
            while (_pendingItems.TryDequeue(out PendingItem pending))
            {
                var item = dataManager.GetItemById((int)pending.ItemId);
                if (item == null) { LogMessage($"[AP] unknown item id {pending.ItemId}"); continue; }

                try { grantItem(pending.Index, item, pending.Sender); }
                catch (Exception ex) { LogMessage($"[AP] grant '{item.Name}' failed: " + ex); }
            }

            while (_pendingCheckedLocations.TryDequeue(out long locId))
            {
                var loc = dataManager.GetLocationById((int)locId);
                if (loc == null) continue;
                try { markLocationChecked(loc); }
                catch (Exception ex) { LogMessage($"[AP] mark location {locId} failed: " + ex); }
            }
        }

        /// <summary>
        /// Sends a location check. Safe to call from the game thread; deduplicated,
        /// and deferred rather than lost if we aren't connected yet.
        /// </summary>
        public void SendLocationCheck(long locationId)
        {
            lock (_sentLock)
            {
                if (!_sentChecks.Add(locationId))   // already sent
                {
                    var l = dataManager.GetLocationById((int)locationId);
                    LogMessage($"[RESPAWN] location {locationId} \"{l?.Name}\" checked twice");
                    return;
                }
            }

            if (!IsConnected || _session == null)
            {
                _deferredChecks.Enqueue(locationId);
                LogMessage($"[AP] not connected; deferring check for location {locationId}");
                return;
            }

            try { _session.Locations.CompleteLocationChecks(locationId); }
            catch (Exception ex) { LogMessage($"[AP] failed to send check {locationId}: " + ex); }
        }

        public void SendLocationCheck(Location location) => SendLocationCheck(location.Id);

        // ---- scouting ----

        /// <summary>
        /// What each scouted location holds. Written on the scout's completion
        /// thread, read on the game thread while drawing the shop.
        /// </summary>
        private readonly ConcurrentDictionary<long, ScoutedOffer> _scouted = new();

        /// <summary>
        /// Asks the server what these locations hold. Fire-and-forget: the task
        /// is never awaited, so the game thread never waits on the network.
        ///
        /// asHint creates a public hint the trackers can see. Without it the
        /// scout is private, which is how the shop knows what an offer sends
        /// before the player has laid eyes on it. CreateAndAnnounceOnce makes
        /// the public form idempotent, so re-hinting an offer the player has
        /// already seen doesn't spam anyone's chat.
        ///
        /// Returns false when nothing was sent, so the caller can retry later.
        /// </summary>
        public bool ScoutLocations(IReadOnlyList<long> locationIds, bool asHint)
        {
            if (locationIds.Count == 0) return true;

            if (!IsConnected || _session == null)
            {
                LogMessage($"[AP] not connected; dropping {locationIds.Count} scout(s)");
                return false;
            }

            var ids = new long[locationIds.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = locationIds[i];

            string what = asHint ? "hinted" : "scouted";

            try
            {
                var policy = asHint ? HintCreationPolicy.CreateAndAnnounceOnce : HintCreationPolicy.None;

                _ = _session.Locations.ScoutLocationsAsync(policy, ids).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        LogMessage($"[AP] scout failed: {t.Exception?.GetBaseException().Message}");
                        return;
                    }

                    foreach (var kv in t.Result)
                    {
                        var info = kv.Value;

                        // Our own items read better without "(ourselves)" after them.
                        string name = info.IsReceiverRelatedToActivePlayer
                            ? info.ItemDisplayName
                            : $"{info.ItemDisplayName} ({info.Player.Alias})";

                        // The first line leads because the shop panel prefixes
                        // it with "Effect: " for modifier offers.
                        string description =
                            $"{Classify(info.Flags)} item\n" +
                            $"For: {info.Player.Alias}\n" +
                            $"Game: {info.ItemGame}";

                        _scouted[kv.Key] = new ScoutedOffer(name, description);
                    }

                    LogMessage($"[AP] {what} {ids.Length} location(s)");
                });

                return true;
            }
            catch (Exception ex)
            {
                LogMessage("[AP] scout failed: " + ex);
                return false;
            }
        }

        /// <summary>Item name for a scouted location, or null if not scouted yet.</summary>
        public string? ScoutedName(long locationId) =>
            _scouted.TryGetValue(locationId, out var s) ? s.Name : null;

        /// <summary>Recipient, game and classification, or null if not scouted yet.</summary>
        public string? ScoutedDescription(long locationId) =>
            _scouted.TryGetValue(locationId, out var s) ? s.Description : null;

        /// <summary>
        /// Forgets every scout. A different multiworld gives different answers,
        /// so this belongs with whatever else is reset when one ends.
        /// </summary>
        public void ClearScouts() => _scouted.Clear();

        /// <summary>Archipelago's item flags as the single word a player expects.</summary>
        private static string Classify(ItemFlags flags)
        {
            if ((flags & ItemFlags.Trap) != 0) return "Trap";
            if ((flags & ItemFlags.Advancement) != 0) return "Progression";
            if ((flags & ItemFlags.NeverExclude) != 0) return "Useful";
            return "Filler";
        }

        private bool _goalSent;

        public void ClearGoalSent() => _goalSent = false;

        /// <summary>
        /// Tells the server this slot has finished its game. Idempotent per
        /// session - the server tolerates a repeat, but the pump would otherwise
        /// send one every frame. Returns false if it couldn't go out, so the
        /// caller can try again later.
        /// </summary>
        public bool SendGoalCompletion()
        {
            if (_goalSent) return true;
            if (!IsConnected || _session == null) return false;

            try
            {
                _session.Socket.SendPacket(new StatusUpdatePacket
                {
                    Status = ArchipelagoClientState.ClientGoal
                });

                _goalSent = true;
                LogMessage("[AP] goal reported");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage("[AP] goal report failed: " + ex);
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Shared data storage
        //
        // The browser map reads these keys, so the naming is a contract with it
        // rather than something we can change unilaterally:
        //
        //     Mio_<slot>_<team>_<key>
        //
        // Only "events" exists today - the bosses the player has beaten, as
        // strings the map understands ("Ati Defeated"). Other keys slot in
        // without touching this code.
        //
        // Global scope on purpose: Scope.Slot would have the server prefix the
        // key with the slot, which would break the agreed name.
        // ---------------------------------------------------------------

        public const string EventsKey = "events";

        private string? DataStoreKey(string key) =>
            _session == null
                ? null
                : $"Mio_{_session.ConnectionInfo.Slot}_{_session.ConnectionInfo.Team}_{key}";

        /// <summary>
        /// Adds an entry to one of the map's shared lists, creating the key if the
        /// server doesn't have it yet. Idempotent: an entry already present - put
        /// there by us on an earlier session, or by hand on the map - is left
        /// alone, so reconnects can't duplicate it.
        /// </summary>
        public bool AddToList(string key, string entry, string? bounceType = null)
        {
            if (!IsConnected || _session == null) return false;

            string? name = DataStoreKey(key);
            if (name == null) return false;

            try
            {
                // No-op once the key has a value, so this is safe on every call and
                // is what creates the list on a fresh multiworld.
                _session.DataStorage[Scope.Global, name].Initialize(new JArray());

                _ = _session.DataStorage[Scope.Global, name].GetAsync<string[]>()
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            LogMessage($"[AP] datastore read failed: {t.Exception?.GetBaseException().Message}");
                            return;
                        }

                        try
                        {
                            string[]? current = t.Result;
                            if (current != null && Array.IndexOf(current, entry) >= 0) return;

                            _session.DataStorage[Scope.Global, name] += new[] { entry };
                            LogMessage($"[AP] {key}: recorded \"{entry}\"");

                            if (bounceType != null) SendBounce(bounceType);
                        }
                        catch (Exception ex) { LogMessage("[AP] datastore append failed: " + ex); }
                    });

                return true;
            }
            catch (Exception ex)
            {
                LogMessage("[AP] datastore write failed: " + ex);
                return false;
            }
        }

        /// <summary>Bounce type that tells the browser map to re-read its cache.</summary>
        public const string UpdateEventsBounce = "UpdateEvents";

        /// <summary>
        /// Asks other clients on this slot to refresh. Deliberately not public to
        /// the pump: a bounce is only correct once the write it announces has gone
        /// out, so it is sent from the datastore continuation rather than beside it.
        /// </summary>
        private void SendBounce(string bounceType)
        {
            if (!IsConnected || _session == null) return;
            try
            {
                _session.Socket.SendPacket(new BouncePacket
                {
                    Slots = new List<int> { _session.ConnectionInfo.Slot },
                    Data = new Dictionary<string, JToken> { ["bounce_type"] = bounceType },
                });
            }
            catch (Exception ex) { LogMessage("[AP] bounce failed: " + ex); }
        }


        public void SendDeathLink(string cause = "Died :(")
        {
            if (!IsConnected || _session == null || !isDeathLink) return;

            deathLinkAmnestyPoints++;
            LogMessage("amnesty: " + deathLinkAmnestyPoints.ToString());
            if (deathLinkAmnestyPoints < deathLinkAmnesty) return;
            deathLinkAmnestyPoints = 0;

            try
            {
                lastDeathLinkTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _session.Socket.SendPacket(new BouncePacket
                {
                    Tags = new List<string> { "DeathLink" },
                    Data = new Dictionary<string, JToken>
                    {
                        ["source"] = _session.Players.ActivePlayer.Name,
                        ["cause"] = cause,
                        ["time"] = lastDeathLinkTime
                    }
                });
            }
            catch (Exception ex) { LogMessage("[AP] deathlink bounce failed: " + ex); }
        }


        public void SendDamageLink(int shieldsLost)
        {
            if (!IsConnected || _session == null || !isDamageLink) return;

            try
            {
                lastDamageLinkTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _session.Socket.SendPacket(new BouncePacket
                {
                    Tags = new List<string> { "SharedDamage" },
                    Data = new Dictionary<string, JToken>
                    {
                        ["time"] = lastDamageLinkTime,
                        ["uuid"] = uuid.ToString(),
                        ["source"] = _session.Players.ActivePlayer.Name,
                        ["damage_points"] = DamageLinkThreshold * shieldsLost
                    }
                });
            }
            catch (Exception ex) { LogMessage("[AP] damagelink bounce failed: " + ex); }
        }



        /// <summary>
        /// {
        ///   "bounce_type": "TriggerPopup",
        ///   "text": "Wow a popup", # the text
        ///   "color": "#FF0000", # color it shows in
        ///   "persistent": false, # whether the player needs to dismiss it
        /// }
        /// </summary>
        private void SendPopupToMap(string message, string hexCode = "#FFFFFF", bool persistent = false)
        {
            if (!IsConnected || _session == null) return;

            try
            {
                _session.Socket.SendPacket(new BouncePacket
                {
                    Slots = new List<int> { _session.ConnectionInfo.Slot },
                    Data = new Dictionary<string, JToken>
                    {
                        ["bounce_type"] = "TriggerPopup",
                        ["text"] = message,
                        ["color"] = hexCode,
                        ["persistent"] = persistent,
                    },
                });
            }
            catch (Exception ex) { LogMessage("[AP] popup bounce failed: " + ex); }
        }


        private void ReceiveBounce(ArchipelagoPacketBase packet)
        {
            switch (packet)
            {
                case BouncedPacket bouncePacket:
                    if (bouncePacket.Tags.Count != 0)
                    {
                        switch (bouncePacket.Tags[0])
                        {
                            case "DeathLink":
                                break;
                            case "KnockbackLink":
                                if (!isKnockbackLink) break;

                                Dictionary<string, float> values = bouncePacket.Data["value"].ToObject<Dictionary<string, float>>();
                                MioGame.Vec_float_3 pos = new();
                                pos.x = values["x"];
                                pos.y = values["y"];
                                queuedKnockbackLink = AddVecFloat3(queuedKnockbackLink, pos);
                                break;
                            case "SharedDamage":
                                if (!isDamageLink) break;
                                if (Math.Abs(bouncePacket.Data["time"].ToObject<int>() - lastDamageLinkTime) < 2) break;

                                int newPoints = bouncePacket.Data["damage_points"].ToObject<int>();
                                damageLinkPoints += newPoints;
                                break;
                        }
                    }
                    if (bouncePacket.Data.ContainsKey("bounce_type")) 
                    {
                        switch (bouncePacket.Data["bounce_type"].Value<string>())
                        {
                            case "LogMessage":
                                LogMessage(bouncePacket.Data["text"].Value<string>());
                                break;
                        }
                    }
                    break;
            }
        }


        // <summary>The slot data (player options, as received from the server)</summary>
        private Dictionary<string, dynamic> _cachedSlotData = [];


        // <summary>
        // Requests the player's slot data from the server, setting the cache when received.
        // </summary>
        private async Task CacheSlotData()
        {
            if (!IsConnected || _session == null) return;

            try
            {
                _cachedSlotData = await _session.DataStorage.GetSlotDataAsync(_session.ConnectionInfo.Slot);
            }
            catch (Exception ex)
            {
                LogMessage("[AP] could not cache slot data: " + ex);
            }
        }


        // <summary>
        // Gets a slot data value from its key, returning the fallback if it cannot access it.
        // </summary>
        public dynamic GetSlotDataValue(string key, dynamic fallback)
        {
            if (!IsConnected || _session == null || !_cachedSlotData.ContainsKey(key)) return fallback;

            try
            {
                return _cachedSlotData[key];
            }
            catch (Exception ex) { 
                LogMessage($"[AP] could not access slot data (for key {key}): " + ex); 
            }
            return fallback;
        }

        public List<string> connectionTags = [];

        public void UpdateTag(string tag, bool on)
        {
            if (connectionTags.Count > 0) LogMessage(connectionTags[0]);
            
            if (on) connectionTags.Add(tag);
            else connectionTags.Remove(tag);
            try
            {
                _session.ConnectionInfo.UpdateConnectionOptions([.. connectionTags]);
            }
            catch (Exception ex) { LogMessage("[AP] could not set tags: " + ex); }
        }


        public Guid uuid = Guid.NewGuid();

        public bool isDeathLink = false;
        public bool queuedDeathLink = false;
        public long lastDeathLinkTime = 0;
        public unsafe DeathLinkService? deathLinkService;
        public int deathLinkAmnesty = 1;
        public int deathLinkGrace = 1;
        public int deathLinkAmnestyPoints = 1;
        public int deathLinkGracePoints = 1;

        public bool isKnockbackLink;
        public MioGame.Vec_float_3 queuedKnockbackLink;

        public bool isDamageLink;
        public int damageLinkPoints = 0;
        public const int DamageLinkThreshold = 100;
        public long lastDamageLinkTime = 0;


        private void ReceiveDeathLink(DeathLink deathLink)
        {
            if (!isDeathLink) return;
            if (Math.Abs(deathLink.Timestamp.ToUnixTimeStamp() - lastDeathLinkTime) < 2) return;
            deathLinkGracePoints++;
            if (deathLinkGracePoints >= deathLinkGrace)
            {
                queuedDeathLink = true;
                deathLinkGracePoints = 0;
            }
            LogMessage($"[AP] DeathLink received from {deathLink.Source}: {deathLink.Cause}");
        }


        public static MioGame.Vec_float_3 AddVecFloat3(MioGame.Vec_float_3 vec1, MioGame.Vec_float_3 vec2)
        {
            MioGame.Vec_float_3 result = new();
            result.x = vec1.x + vec2.x;
            result.y = vec1.y + vec2.y;
            result.z = vec1.z + vec2.z;
            return result;
        }
        public static MioGame.Vec_float_3 MultiplyVecFloat3(MioGame.Vec_float_3 vec, float multiplier)
        {
            vec.x *= multiplier; vec.y *= multiplier; vec.z *= multiplier;
            return vec;
        }
        public static bool IsVecFloat3Zero(MioGame.Vec_float_3 vec)
        {
            return IsFloatZero(vec.x) && IsFloatZero(vec.y) && IsFloatZero(vec.z);
        }
        private static bool IsFloatZero(float value)
        {
            return Math.Abs(value) < 0.00001f;
        }
    }
}