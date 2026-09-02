using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MioAP
{
    internal class DataManager
    {
        private readonly Action<string> _loggingCBMethod;
        private JsonSchema jsonData;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public DataManager(Action<string> loggingCBMethod)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            _loggingCBMethod = loggingCBMethod;

            using (StreamReader file = System.IO.File.OpenText(@".\mods\MioAP\client.json"))
            {
                JsonSerializer serializer = new JsonSerializer();
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8601 // Possible null reference assignment.
                jsonData = (JsonSchema)serializer.Deserialize(file, typeof(JsonSchema));
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning restore CS8601 // Possible null reference assignment.
            }
        }

        private void LogMessage(string message)
        {
            _loggingCBMethod?.Invoke(message);
        }

        public Item? GetItemById(int item_id)
        {
            foreach (var item in jsonData.Items)
            {
                if (item.Id == item_id)
                {
                    return item;
                }
            }
            return null;
        }

        public Item? GetItemByName(string item_name)
        {
            foreach (var item in jsonData.Items)
            {
                if (item.Name == item_name)
                {
                    return item;
                }
            }
            return null;
        }

        public Item? GetItemBySaveEntry(string save_entry)
        {
            foreach (var item in jsonData.Items)
            {
                if (item.SaveEntry == save_entry)
                {
                    return item;
                }
            }
            return null;
        }

        public bool CheckItemInItemListBySaveEntry(string save_entry)
        {
            foreach (var item in jsonData.Items)
            {
                if (item.SaveEntry == save_entry)
                {
                    return true;
                }
            }
            return false;
        }

        public Location? GetLocationById(int location_id)
        {
            foreach (var location in jsonData.Locations)
            {
                if (location.Id == location_id)
                {
                    return location;
                }
            }
            return null;
        }

        public Location? GetLocationByName(string location_name)
        {
            foreach (var location in jsonData.Locations)
            {
                if (location.Name == location_name)
                {
                    return location;
                }
            }
            return null;
        }

        public Location? GetLocationByVanillaSaveEntry(string save_entry)
        {
            foreach (var location in jsonData.Locations)
            {
                var item = GetItemById(location.VanillaItem.ItemId);
                if (item.SaveEntry == save_entry)
                {
                    return location;
                }
            }
            return null;
        }

        public Room? GetRoomById(int room_id)
        {
            foreach (var room in jsonData.Rooms)
            {
                if (room.Id == room_id)
                {
                    return room;
                }
            }
            return null;
        }

        public Room? GetRoomByName(string room_name)
        {
            foreach (var room in jsonData.Rooms)
            {
                if (room.Name == room_name)
                {
                    return room;
                }
            }
            return null;
        }

        public Event? GetEventById(int event_id)
        {
            foreach (var apEvent in jsonData.Events)
            {
                if (apEvent.Id == event_id)
                {
                    return apEvent;
                }
            }
            return null;
        }

        public Event? GetEventByName(string event_name)
        {
            foreach (var apEvent in jsonData.Events)
            {
                if (apEvent.ItemName == event_name || apEvent.LocationName == event_name)
                {
                    return apEvent;
                }
            }
            return null;
        }
    }
}
