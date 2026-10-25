using MioModdingApi;

namespace MioAP
{
    /// <summary>
    /// The mod's page in the in-game Mods menu.
    /// </summary>
    internal static class ModOptions
    {
        private static NumberOption? toastDuration;

        /// <summary>Registers the mod's page.</summary>
        internal static void Register(MioAPMod mod)
        {
            var page = ModMenu.RegisterMod(mod, displayName: "Archipelago", "ArchipelagoMW Randomizer settings.");

            toastDuration = page.RegisterNumber(
                "toast_seconds", "Toast duration",
                1, 20, 1, 5,
                "The amount of seconds AP toast notifications will persist."
                );
        }

        /// <summary>Applies a toggle's current value now and again whenever it changes.</summary>
        internal static void Bind(ToggleOption toggle, Action<bool> apply)
        {
            apply(toggle.Value);
            toggle.Changed += apply;
        }

        /// <summary>Applies a number option's current value now and again whenever it changes.</summary>
        internal static void Bind(NumberOption number, Action<int> apply)
        {
            apply(number.Value);
            number.Changed += apply;
        }

        internal static void CreateOptionBindings(GuiManager guiManager)
        {
#pragma warning disable CS8604 // Possible null reference argument.
            Bind(toastDuration, guiManager.SetToastDuration);
#pragma warning restore CS8604 // Possible null reference argument.
        }
    }
}
