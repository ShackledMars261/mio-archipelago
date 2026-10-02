using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace MioAP
{
    /// <summary>Connection details for one Mio save slot.</summary>
    internal sealed class ConnectionDetails
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Slot { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>Save.id of the save these details belong to; 0 if not yet seen.</summary>
        public ulong SaveId { get; set; }

        public bool IsUsable =>
            !string.IsNullOrWhiteSpace(Host)
            && !string.IsNullOrWhiteSpace(Slot)
            && Port > 0 && Port < 65536;

        public override string ToString() => $"{Slot}@{Host}:{Port}";
    }

    /// <summary>
    /// Remembers the last connection that worked for each Mio save slot, so a
    /// returning player can load their save instead of retyping everything.
    ///
    /// This lives beside the mod's other data rather than inside the .save
    /// file, because a Save_entry has no string storage - only flags, an int
    /// count, and a few fixed struct payloads. A sidecar is also easier to fix
    /// by hand when a room moves or a port changes.
    ///
    /// Entries are keyed by save slot index, which means deleting a save and
    /// starting a different multiworld in the same slot would otherwise
    /// auto-connect to the old room. Forget() exists for that case.
    ///
    /// Every operation is best-effort: this is a convenience, and a broken or
    /// unwritable file must never stop someone playing.
    /// </summary>
    internal sealed class ConnectionStore
    {
        private const string FilePath = @".\mods\MioAP\saved_connections.json";

        private readonly Action<string> _log;
        private Dictionary<string, ConnectionDetails> _bySlot = new();

        public ConnectionStore(Action<string> log)
        {
            _log = log;
            Load();
        }

        /// <summary>Stored details for a slot, or null if there are none usable.</summary>
        public ConnectionDetails? ForSaveSlot(uint slot)
        {
            if (!_bySlot.TryGetValue(slot.ToString(), out var d)) return null;
            return d != null && d.IsUsable ? d : null;
        }

        /// <summary>Records detials that are known to have connected successfully.</summary>
        public void Remember(uint slot, ConnectionDetails details)
        {
            if (!details.IsUsable) return;

            _bySlot[slot.ToString()] = details;
            Save();
            _log($"[conn] remembered {details} for save slot {slot}");
        }

        /// <summary>Drops a slot's details, e.g. when a new game is started there.</summary>
        public void Forget(uint slot)
        {
            if (_bySlot.Remove(slot.ToString()))
            {
                Save();
                _log($"[conn] cleared stored details for save slot {slot}");
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                string json = File.ReadAllText(FilePath);
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, ConnectionDetails>>(json);
                if (parsed != null) _bySlot = parsed;

                _log($"[conn] loaded details for {_bySlot.Count} save slot(s)");
            }
            catch (Exception ex)
            {
                // A corrupt file just means we ask the player to type it again.
                _log("[conn] could not read " + FilePath + ": " + ex.Message);
                _bySlot = new Dictionary<string, ConnectionDetails>();
            }
        }

        private void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_bySlot, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log("[conn] could not write " + FilePath + ": " + ex.Message);
            }
        }
    }
}
