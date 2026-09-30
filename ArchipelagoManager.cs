using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MioAP
{
    /// <summary>
    /// Owns the Archipelago session and the boundary between its threads and the
    /// game's.
    ///
    /// THREADING. Every network callback arrives on the websocket thread and does
    /// exactly one thing: enqueue. Nothing touches the game's save from there. The
    /// game thread drains those queues once per fixed_update, which is the only
    /// place save writes happen, so no lock is ever held across native code.
    /// Connecting also runs on its own thread, so the GUI never stalls on a bad
    /// host or a slow handshake.
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
    /// </summary>
    internal class ArchipelagoManager
    {
        private readonly Action<string> _loggingCBMethod;
        private readonly DataManager dataManager;

        private ArchipelagoSession? _session;
        private volatile bool _connected;

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

        public bool IsConnected => _connected;

        public ArchipelagoManager(Action<string> loggingCBMethod, DataManager dataManager)
        {
            _loggingCBMethod = loggingCBMethod;
            this.dataManager = dataManager;
        }

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        /// <summary>
        /// Non-blocking. Starts the connection on a background thread so the
        /// caller (the GUI, on the render thread) never stalls on network I/O.
        /// </summary>
        public void Connect(string hostname, int port, string username, string password)
        {
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
                    string msg = $"Failed to connect to {hostname}:{port} as {username}:";
                    foreach (string error in failure.Errors) msg += $"\n    {error}";
                    foreach (ConnectionRefusedError error in failure.ErrorCodes) msg += $"\n    {error}";
                    LogMessage(msg);
                    return;
                }

                _session = session;
                _connected = true;

                var success = (LoginSuccessful)result;
                LogMessage($"Connected to {hostname}:{port} as \"{username}\". (Slot #{success.Slot})");

                ResyncFromServer();
            }
            catch (Exception ex)
            {
                LogMessage("Connect failed: " + ex);
            }
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

            if (!_connected || _session == null)
            {
                _deferredChecks.Enqueue(locationId);
                LogMessage($"[AP] not connected; deferring check for location {locationId}");
                return;
            }

            try { _session.Locations.CompleteLocationChecks(locationId); }
            catch (Exception ex) { LogMessage($"[AP] failed to send check {locationId}: " + ex); }
        }

        public void SendLocationCheck(Location location) => SendLocationCheck(location.Id);
    }
}