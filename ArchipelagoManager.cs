using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

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
        }

        private void ResyncFromServer()
        {
            if (_session == null) return;

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
    }
}