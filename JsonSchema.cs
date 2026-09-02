using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MioAP
{
    // JsonData myDeserializedClass = JsonConvert.DeserializeObject<JsonData>(myJsonResponse);
    public class Args
    {
        [JsonConstructor]
        public Args(
            [JsonProperty("item_name")] string itemName,
            [JsonProperty("count")] int count,
            [JsonProperty("item_names")] List<string> itemNames
        )
        {
            this.ItemName = itemName;
            this.Count = count;
            this.ItemNames = itemNames;
        }

        [JsonProperty("item_name")]
        public string ItemName { get; }

        [JsonProperty("count")]
        public int Count { get; }

        [JsonProperty("item_names")]
        public IReadOnlyList<string> ItemNames { get; }
    }

    public class Child
    {
        [JsonConstructor]
        public Child(
            [JsonProperty("rule")] string rule,
            [JsonProperty("options")] List<object> options,
            [JsonProperty("args")] Args args,
            [JsonProperty("children")] List<Child> children
        )
        {
            this.Rule = rule;
            this.Options = options;
            this.Args = args;
            this.Children = children;
        }

        [JsonProperty("rule")]
        public string Rule { get; }

        [JsonProperty("options")]
        public IReadOnlyList<object> Options { get; }

        [JsonProperty("args")]
        public Args Args { get; }

        [JsonProperty("children")]
        public IReadOnlyList<Child> Children { get; }
    }

    public class Event
    {
        [JsonConstructor]
        public Event(
            [JsonProperty("eventId")] int eventId,
            [JsonProperty("eventLocationName")] string eventLocationName,
            [JsonProperty("eventItemName")] string eventItemName,
            [JsonProperty("id")] int id,
            [JsonProperty("roomId")] int roomId,
            [JsonProperty("roomName")] string roomName,
            [JsonProperty("locationName")] string locationName,
            [JsonProperty("itemName")] string itemName,
            [JsonProperty("saveEntry")] string saveEntry,
            [JsonProperty("requirements")] Requirements requirements
        )
        {
            this.EventId = eventId;
            this.EventLocationName = eventLocationName;
            this.EventItemName = eventItemName;
            this.Id = id;
            this.RoomId = roomId;
            this.RoomName = roomName;
            this.LocationName = locationName;
            this.ItemName = itemName;
            this.SaveEntry = saveEntry;
            this.Requirements = requirements;
        }

        [JsonProperty("eventId")]
        public int EventId { get; }

        [JsonProperty("eventLocationName")]
        public string EventLocationName { get; }

        [JsonProperty("eventItemName")]
        public string EventItemName { get; }

        [JsonProperty("id")]
        public int Id { get; }

        [JsonProperty("roomId")]
        public int RoomId { get; }

        [JsonProperty("roomName")]
        public string RoomName { get; }

        [JsonProperty("locationName")]
        public string LocationName { get; }

        [JsonProperty("itemName")]
        public string ItemName { get; }

        [JsonProperty("saveEntry")]
        public string SaveEntry { get; }

        [JsonProperty("requirements")]
        public Requirements Requirements { get; }
    }

    public class Item
    {
        [JsonConstructor]
        public Item(
            [JsonProperty("id")] int id,
            [JsonProperty("name")] string name,
            [JsonProperty("classification")] int classification,
            [JsonProperty("saveEntry")] string saveEntry,
            [JsonProperty("count")] int count,
            [JsonProperty("vanillaLocations")] List<VanillaLocation> vanillaLocations
        )
        {
            this.Id = id;
            this.Name = name;
            this.Classification = classification;
            this.SaveEntry = saveEntry;
            this.Count = count;
            this.VanillaLocations = vanillaLocations;
        }

        [JsonProperty("id")]
        public int Id { get; }

        [JsonProperty("name")]
        public string Name { get; }

        [JsonProperty("classification")]
        public int Classification { get; }

        [JsonProperty("saveEntry")]
        public string SaveEntry { get; }

        [JsonProperty("count")]
        public int Count { get; }

        [JsonProperty("vanillaLocations")]
        public IReadOnlyList<VanillaLocation> VanillaLocations { get; }
    }

    public class Location
    {
        [JsonConstructor]
        public Location(
            [JsonProperty("locationId")] int locationId,
            [JsonProperty("locationName")] string locationName,
            [JsonProperty("id")] int id,
            [JsonProperty("name")] string name,
            [JsonProperty("roomId")] int roomId,
            [JsonProperty("roomName")] string roomName,
            [JsonProperty("vanillaItem")] VanillaItem vanillaItem,
            [JsonProperty("requirements")] Requirements requirements
        )
        {
            this.LocationId = locationId;
            this.LocationName = locationName;
            this.Id = id;
            this.Name = name;
            this.RoomId = roomId;
            this.RoomName = roomName;
            this.VanillaItem = vanillaItem;
            this.Requirements = requirements;
        }

        [JsonProperty("locationId")]
        public int LocationId { get; }

        [JsonProperty("locationName")]
        public string LocationName { get; }

        [JsonProperty("id")]
        public int Id { get; }

        [JsonProperty("name")]
        public string Name { get; }

        [JsonProperty("roomId")]
        public int RoomId { get; }

        [JsonProperty("roomName")]
        public string RoomName { get; }

        [JsonProperty("vanillaItem")]
        public VanillaItem VanillaItem { get; }

        [JsonProperty("requirements")]
        public Requirements Requirements { get; }
    }

    public class Requirements
    {
        [JsonConstructor]
        public Requirements(
            [JsonProperty("rule")] string rule,
            [JsonProperty("options")] List<object> options,
            [JsonProperty("args")] Args args,
            [JsonProperty("children")] List<Child> children
        )
        {
            this.Rule = rule;
            this.Options = options;
            this.Args = args;
            this.Children = children;
        }

        [JsonProperty("rule")]
        public string Rule { get; }

        [JsonProperty("options")]
        public IReadOnlyList<object> Options { get; }

        [JsonProperty("args")]
        public Args Args { get; }

        [JsonProperty("children")]
        public IReadOnlyList<Child> Children { get; }
    }

    public class Room
    {
        [JsonConstructor]
        public Room(
            [JsonProperty("id")] int id,
            [JsonProperty("name")] string name,
            [JsonProperty("transitions")] List<Transition> transitions,
            [JsonProperty("locations")] List<Location> locations,
            [JsonProperty("events")] List<Event> events
        )
        {
            this.Id = id;
            this.Name = name;
            this.Transitions = transitions;
            this.Locations = locations;
            this.Events = events;
        }

        [JsonProperty("id")]
        public int Id { get; }

        [JsonProperty("name")]
        public string Name { get; }

        [JsonProperty("transitions")]
        public IReadOnlyList<Transition> Transitions { get; }

        [JsonProperty("locations")]
        public IReadOnlyList<Location> Locations { get; }

        [JsonProperty("events")]
        public IReadOnlyList<Event> Events { get; }
    }

    public class JsonSchema
    {
        [JsonConstructor]
        public JsonSchema(
            [JsonProperty("version")] string version,
            [JsonProperty("timestamp")] string timestamp,
            [JsonProperty("rooms")] List<Room> rooms,
            [JsonProperty("transitions")] List<Transition> transitions,
            [JsonProperty("locations")] List<Location> locations,
            [JsonProperty("events")] List<Event> events,
            [JsonProperty("items")] List<Item> items
        )
        {
            this.Version = version;
            this.Timestamp = timestamp;
            this.Rooms = rooms;
            this.Transitions = transitions;
            this.Locations = locations;
            this.Events = events;
            this.Items = items;
        }

        [JsonProperty("version")]
        public string Version { get; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; }

        [JsonProperty("rooms")]
        public IReadOnlyList<Room> Rooms { get; }

        [JsonProperty("transitions")]
        public IReadOnlyList<Transition> Transitions { get; }

        [JsonProperty("locations")]
        public IReadOnlyList<Location> Locations { get; }

        [JsonProperty("events")]
        public IReadOnlyList<Event> Events { get; }

        [JsonProperty("items")]
        public IReadOnlyList<Item> Items { get; }
    }

    public class Transition
    {
        [JsonConstructor]
        public Transition(
            [JsonProperty("transitionId")] int transitionId,
            [JsonProperty("transitionName")] string transitionName,
            [JsonProperty("linkedRoomId")] int linkedRoomId,
            [JsonProperty("linkedRoomName")] string linkedRoomName,
            [JsonProperty("id")] int id,
            [JsonProperty("name")] string name,
            [JsonProperty("fromId")] int fromId,
            [JsonProperty("fromName")] string fromName,
            [JsonProperty("toId")] int toId,
            [JsonProperty("toName")] string toName,
            [JsonProperty("requirements")] Requirements requirements
        )
        {
            this.TransitionId = transitionId;
            this.TransitionName = transitionName;
            this.LinkedRoomId = linkedRoomId;
            this.LinkedRoomName = linkedRoomName;
            this.Id = id;
            this.Name = name;
            this.FromId = fromId;
            this.FromName = fromName;
            this.ToId = toId;
            this.ToName = toName;
            this.Requirements = requirements;
        }

        [JsonProperty("transitionId")]
        public int TransitionId { get; }

        [JsonProperty("transitionName")]
        public string TransitionName { get; }

        [JsonProperty("linkedRoomId")]
        public int LinkedRoomId { get; }

        [JsonProperty("linkedRoomName")]
        public string LinkedRoomName { get; }

        [JsonProperty("id")]
        public int Id { get; }

        [JsonProperty("name")]
        public string Name { get; }

        [JsonProperty("fromId")]
        public int FromId { get; }

        [JsonProperty("fromName")]
        public string FromName { get; }

        [JsonProperty("toId")]
        public int ToId { get; }

        [JsonProperty("toName")]
        public string ToName { get; }

        [JsonProperty("requirements")]
        public Requirements Requirements { get; }
    }

    public class VanillaItem
    {
        [JsonConstructor]
        public VanillaItem(
            [JsonProperty("itemId")] int itemId,
            [JsonProperty("itemName")] string itemName,
            [JsonProperty("itemClassification")] int itemClassification
        )
        {
            this.ItemId = itemId;
            this.ItemName = itemName;
            this.ItemClassification = itemClassification;
        }

        [JsonProperty("itemId")]
        public int ItemId { get; }

        [JsonProperty("itemName")]
        public string ItemName { get; }

        [JsonProperty("itemClassification")]
        public int ItemClassification { get; }
    }

    public class VanillaLocation
    {
        [JsonConstructor]
        public VanillaLocation(
            [JsonProperty("locationId")] int locationId,
            [JsonProperty("locationName")] string locationName
        )
        {
            this.LocationId = locationId;
            this.LocationName = locationName;
        }

        [JsonProperty("locationId")]
        public int LocationId { get; }

        [JsonProperty("locationName")]
        public string LocationName { get; }
    }


}
