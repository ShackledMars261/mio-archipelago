using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace MioAP
{
    internal class DataManager
    {
        /// <summary>
        /// Loads and indexes the generated Archipelago data - client.json for items,
        /// locations and rooms, plus nacre_carcass.json for per-node Nacre entries.
        ///
        /// Everything is indexed once at construction. Lookups happen inside Mio::has
        /// and the loot hook, which are among the hottest functions in the game, so
        /// nothing here may scan a list.
        ///
        /// LOCATION IDENTITY is the hard part. The game tells us which save entry was
        /// looted, but not which location it came from, and a save entry doesn't
        /// uniquely identify one: Crystallized Nacre alone is the vanilla item at 62
        /// of them. Resolution is therefore layered:
        ///
        ///   1. Per-node carcass entries ("CARCASS:0x..."), which Nacre pickups loot
        ///      under. These are exact, and cover all 62.
        ///   2. Room plus save entry, unique for 275 of the 279 locations.
        ///   3. Save entry alone, but only where it maps to exactly one location.
        ///
        /// This class also owns the shadow-entry naming convention (see HooksManager
        /// for what shadow entries are for), so the prefix is defined in exactly one
        /// place.
        /// </summary>
        public const string ShadowPrefix = "ARCHIPELAGO_";

        private readonly Action<string> _loggingCBMethod;
        private readonly JsonSchema jsonData;

        // ---- indexes, built once at load ----
        private readonly Dictionary<int, Item> _itemsById = new();
        private readonly Dictionary<string, Item> _itemsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Item> _itemsBySaveEntry = new(StringComparer.Ordinal);

        private readonly Dictionary<int, Location> _locationsById = new();
        private readonly Dictionary<string, Location> _locationsByName = new(StringComparer.Ordinal);

        // Location lookup keyed on "room|saveEntry". Unique for 275 of 279
        // locations; the ambiguous ones are recorded below.
        private readonly Dictionary<string, Location> _locationsByRoomAndEntry = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguousRoomEntryKeys = new(StringComparer.Ordinal);

        // Fallback: saveEntry -> location, only populated when that save entry
        // maps to exactly one location.
        private readonly Dictionary<string, Location> _uniqueLocationBySaveEntry = new(StringComparer.Ordinal);

        private readonly Dictionary<int, Room> _roomsById = new();
        private readonly Dictionary<string, Room> _roomsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Event> _eventsById = new();

        // Optional per-call-site location identity, for pickups that room + save entry
        // cannot disambiguate. Key: "scopeName|saveEntry".
        private readonly Dictionary<string, Location> _locationsByScope = new(StringComparer.Ordinal);

        // Nacre pickups don't loot as RESOURCE:SOLID_DROPLETS; each has its own
        // per-node save entry, "CARCASS:0x<hash>". That gives exact location identity.
        private readonly Dictionary<string, Location> _locationsByCarcass = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _carcassByLocationId = new();

        private readonly Dictionary<string, List<string>> _entriesByCategory = new(StringComparer.Ordinal);


        public DataManager(Action<string> loggingCBMethod)
        {
            _loggingCBMethod = loggingCBMethod;

            using (StreamReader file = File.OpenText(@".\mods\MioAP\client.json"))
            {
                var serializer = new JsonSerializer();
                jsonData = (JsonSchema)serializer.Deserialize(file, typeof(JsonSchema))!;
            }

            BuildIndexes();

            LoadCarcassMap();
        }

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        private void BuildIndexes()
        {
            foreach (var item in jsonData.Items)
            {
                _itemsById[item.Id] = item;
                _itemsByName[item.Name] = item;
                if (!string.IsNullOrEmpty(item.SaveEntry))
                    _itemsBySaveEntry[item.SaveEntry] = item;

                int colon = item.SaveEntry.IndexOf(':');
                if (colon > 0)
                {
                    string category = item.SaveEntry.Substring(0, colon);
                    if (!_entriesByCategory.TryGetValue(category, out var list))
                        _entriesByCategory[category] = list = new List<string>();
                    list.Add(item.SaveEntry);
                }
            }

            // Count how many locations share each save entry, so we know which
            // ones can safely be reverse-looked-up by entry alone.
            var perEntry = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var loc in jsonData.Locations)
            {
                _locationsById[loc.Id] = loc;
                _locationsByName[loc.Name] = loc;

                string? entry = SaveEntryForLocation(loc);
                if (entry == null) continue;

                perEntry.TryGetValue(entry, out int n);
                perEntry[entry] = n + 1;

                string key = RoomEntryKey(loc.RoomName, entry);
                if (_locationsByRoomAndEntry.ContainsKey(key)) _ambiguousRoomEntryKeys.Add(key);
                else _locationsByRoomAndEntry[key] = loc;
            }

            foreach (var loc in jsonData.Locations)
            {
                string? entry = SaveEntryForLocation(loc);
                if (entry != null && perEntry[entry] == 1)
                    _uniqueLocationBySaveEntry[entry] = loc;
            }

            foreach (var room in jsonData.Rooms)
            {
                _roomsById[room.Id] = room;
                _roomsByName[room.Name] = room;
            }

            foreach (var ev in jsonData.Events)
                _eventsById[ev.Id] = ev;

            LogMessage($"[data] indexed {_itemsById.Count} items, {_locationsById.Count} locations, " +
                       $"{_roomsById.Count} rooms  ({_ambiguousRoomEntryKeys.Count} ambiguous room+entry keys)");
        }

        private void LoadCarcassMap()
        {
            string path = @".\mods\MioAP\nacre_carcass.json";
            if (!File.Exists(path))
            {
                LogMessage($"[data] WARNING: {path} not found - Nacre pickups will not be checked");
                return;
            }

            Dictionary<string, string>? map;
            using (StreamReader file = File.OpenText(path))
            {
                var serializer = new JsonSerializer();
                map = (Dictionary<string, string>?)serializer.Deserialize(
                    file, typeof(Dictionary<string, string>));
            }
            if (map == null) return;

            int matched = 0;
            foreach (var kv in map)
            {
                if (_locationsByName.TryGetValue(kv.Key, out var loc))
                {
                    _locationsByCarcass[kv.Value] = loc;
                    _carcassByLocationId[loc.Id] = kv.Value;
                    matched++;
                }
                else
                {
                    LogMessage($"[data] carcass map references unknown location: \"{kv.Key}\"");
                }
            }

            LogMessage($"[data] carcass map: {matched}/{map.Count} entries matched to locations");
        }

        private string? SaveEntryForLocation(Location loc)
        {
            if (loc.VanillaItem == null) return null;
            return _itemsById.TryGetValue(loc.VanillaItem.ItemId, out var item) ? item.SaveEntry : null;
        }

        private static string RoomEntryKey(string room, string saveEntry) => room + "|" + saveEntry;

        /// <summary>Shadow entry for a save entry, e.g. "ARCHIPELAGO_UNLOCK:HOOK".</summary>
        public static string ShadowEntry(string saveEntry) => ShadowPrefix + saveEntry;

        // ---- item lookups ----

        public Item? GetItemById(int id) => _itemsById.TryGetValue(id, out var i) ? i : null;
        public Item? GetItemByName(string name) => _itemsByName.TryGetValue(name, out var i) ? i : null;
        public Item? GetItemBySaveEntry(string saveEntry) =>
            _itemsBySaveEntry.TryGetValue(saveEntry, out var i) ? i : null;

        /// <summary>True if this save entry is managed by Archipelago.</summary>
        public bool IsApItem(string saveEntry) => _itemsBySaveEntry.ContainsKey(saveEntry);

        // ---- location lookups ----

        public Location? GetLocationById(int id) => _locationsById.TryGetValue(id, out var l) ? l : null;
        public Location? GetLocationByName(string name) =>
            _locationsByName.TryGetValue(name, out var l) ? l : null;

        /// <summary>
        /// Best-effort location identity for a pickup. Prefers room + save entry,
        /// which is unique for all but four locations; falls back to save entry
        /// alone when that is unambiguous. Returns null when it cannot tell.
        /// </summary>
        public Location? ResolveLocation(string saveEntry, string? roomName)
        {
            if (roomName != null)
            {
                string key = RoomEntryKey(roomName, saveEntry);
                if (!_ambiguousRoomEntryKeys.Contains(key)
                    && _locationsByRoomAndEntry.TryGetValue(key, out var byRoom))
                    return byRoom;
            }

            if (_uniqueLocationBySaveEntry.TryGetValue(saveEntry, out var byEntry))
                return byEntry;

            return null;
        }

        // ---- rooms / events ----

        public Room? GetRoomById(int id) => _roomsById.TryGetValue(id, out var r) ? r : null;
        public Room? GetRoomByName(string name) => _roomsByName.TryGetValue(name, out var r) ? r : null;
        public Event? GetEventById(int id) => _eventsById.TryGetValue(id, out var e) ? e : null;

        public Event? GetEventByName(string name)
        {
            foreach (var ev in jsonData.Events)
                if (ev.ItemName == name || ev.LocationName == name) return ev;
            return null;
        }

        public IReadOnlyList<Item> AllItems => jsonData.Items;
        public IReadOnlyList<Location> AllLocations => jsonData.Locations;

        // ---- edge cases ----

        public Location? ResolveLocationForScope(string saveEntry, string scopeName) =>
    _locationsByScope.TryGetValue(scopeName + "|" + saveEntry, out var l) ? l : null;

        /// <summary>Registers a location against a specific loot call site.</summary>
        public void RegisterScopeLocation(string scopeName, string saveEntry, int locationId)
        {
            var loc = GetLocationById(locationId);
            if (loc != null) _locationsByScope[scopeName + "|" + saveEntry] = loc;
        }

        /// <summary>Location for a per-node carcass entry, or null if unknown.</summary>
        public Location? GetLocationByCarcassEntry(string saveEntry) =>
            _locationsByCarcass.TryGetValue(saveEntry, out var l) ? l : null;

        public string? GetCarcassEntryForLocation(int locationId) =>
    _carcassByLocationId.TryGetValue(locationId, out var e) ? e : null;

        public bool IsCarcassPickup(string saveEntry) => _locationsByCarcass.ContainsKey(saveEntry);

        /// <summary>Every save entry in a category, e.g. all TRINKET_SLOT_UPGRADE:N.</summary>
        public IReadOnlyList<string> GetEntriesInCategory(string category) =>
            _entriesByCategory.TryGetValue(category, out var l) ? l : Array.Empty<string>();

        /// <summary>
        /// Every distinct vanilla save entry among the locations in one room.
        /// Scans rather than using an index: this builds a scope once at
        /// startup, so it isn't on any hot path.
        /// </summary>
        public string[] GetVanillaEntriesInRoom(string roomName)
        {
            var entries = new HashSet<string>(StringComparer.Ordinal);

            foreach (var loc in jsonData.Locations)
            {
                if (!string.Equals(loc.RoomName, roomName, StringComparison.Ordinal)) continue;

                string? entry = SaveEntryForLocation(loc);
                if (entry != null) entries.Add(entry);
            }

            var result = new string[entries.Count];
            entries.CopyTo(result);
            return result;
        }
    }
}