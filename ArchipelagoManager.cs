using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using MioGame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MioAP
{
    internal class ArchipelagoManager
    {
        private readonly Action<string> _loggingCBMethod;

        private ArchipelagoSession? _session;

        private DataManager dataManager;

        public bool sessionInitialized;

        private List<long> itemCache;

        public ArchipelagoManager(Action<string> loggingCBMethod, DataManager dataManager)
        {
            _loggingCBMethod = loggingCBMethod;
            sessionInitialized = false;
            this.dataManager = dataManager;
            this.itemCache = [];
        }

        private void LogMessage(string message)
        {
            _loggingCBMethod?.Invoke(message);
        }

        public ArchipelagoSession session
        {
            get
            {
                if (sessionInitialized)
                {
#pragma warning disable CS8603 // Possible null reference return.
                    return _session;
#pragma warning restore CS8603 // Possible null reference return.
                }
                else
                {
                    LogMessage("SESSION NOT YET INITIALIZED!!! ");
                    throw new Exception("Archipelago session not yet initialized.");
                }
            }
        }

        private void InitSession(string hostname, int port)
        {
            _session = ArchipelagoSessionFactory.CreateSession(hostname, port);
            _session.MessageLog.OnMessageReceived += OnMessageReceived;
            _session.Items.ItemReceived += ItemReceived;
            sessionInitialized = true;
        }

        private void OnMessageReceived(Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage message)
        {
            LogMessage($"Message from AP Server: \"{message.ToString()}\"");
        }

        private void ItemReceived(Archipelago.MultiClient.Net.Helpers.ReceivedItemsHelper helper)
        {
            var itemReceived = helper.PeekItem();

            if (itemReceived != null)
            {
                AddItemToCacheById(itemReceived.ItemId);
            }

            helper.DequeueItem();
        }


        public void Connect(string hostname, int port, string username, string password)
        {
            InitSession(hostname, port);
            LoginResult result;

            try
            {
                // handle TryConnectAndLogin here and save the returned object to `result`
                result = session.TryConnectAndLogin("Memories in Orbit", username, Archipelago.MultiClient.Net.Enums.ItemsHandlingFlags.AllItems, password: password);
            }
            catch (Exception e)
            {
                result = new LoginFailure(e.GetBaseException().Message);
            }

            if (!result.Successful)
            {
                LoginFailure failure = (LoginFailure)result;
                string errorMessage = $"Failed to Connect to {hostname}:{port} as {username}:";
                foreach (string error in failure.Errors)
                {
                    errorMessage += $"\n    {error}";
                }
                foreach (ConnectionRefusedError error in failure.ErrorCodes)
                {
                    errorMessage += $"\n    {error}";
                }

                LogMessage(errorMessage);


                return;
            }

            var loginSuccess = (LoginSuccessful)result;

            LogMessage($"Connected to {hostname}:{port} as \"{username}\". (Slot #{loginSuccess.Slot})");

            SyncItemCache();

            return;
        }

        public void SyncItemCache()
        {
            LogMessage("Syncing AP Item Cache.");

            List<long> newCache = [];

            foreach (var item in session.Items.AllItemsReceived)
            {
                newCache.Add(item.ItemId);
            }

            itemCache = newCache;

            LogMessage("Done Syncing AP Item Cache.");
        }

        public void AddItemToCache(Item item)
        {
            itemCache.Add(item.Id);
        }

        public void AddItemToCacheById(long item_id)
        {
            if (!itemCache.Contains(item_id))
            {
                itemCache.Add(item_id);
            }
        }

        public bool CheckItemInCacheBySaveEntry(string save_entry)
        {
            Item? item = dataManager.GetItemBySaveEntry(save_entry);
            if (item is null)
            {
                return false;
            }
            return itemCache.Contains(item.Id);
        }


        public bool SendLocationCheckFromVanillaSaveEntry(string save_entry)
        {
            Location? location = dataManager.GetLocationByVanillaSaveEntry(save_entry);
            if (location is null)
            {
                return false;
            }
            session.Locations.CompleteLocationChecks(location.Id);
            return true;
        }
    }
}
