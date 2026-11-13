using MioModdingApi;

namespace MioAP
{
    /// <summary>
    /// The mod's page in the in-game Mods menu.
    /// </summary>
    internal static class ModOptions
    {
        private static KeybindOption? menuToggle;
        private static NumberOption? toastDuration;

        public static ToggleOption? deathLink;
        public static ToggleOption? knockbackLink;
        public static ToggleOption? damageLink;

        private static ButtonOption? teleportToStart;

        /// <summary>Registers the mod's page.</summary>
        internal static void Register(MioAPMod mod)
        {
            var page = ModMenu.RegisterMod(mod, displayName: "Archipelago", "ArchipelagoMW Randomizer settings.");

            menuToggle = page.RegisterKeybind(
                "menu_toggle", "Connection menu",
                defaultKey: MioGame.Key.Key_F1,
                controller: false,        // keyboard/mouse row only
                description: "Opens and closes the Archipelago connection menu."
                );
            toastDuration = page.RegisterNumber(
                "toast_seconds", "Toast duration",
                1, 20, 1, 5,
                "The amount of seconds AP toast notifications will persist."
                );
            deathLink = page.RegisterToggle(
                "death_link", "DeathLink",
                false,
                "Toggles DeathLink."
                );
            knockbackLink = page.RegisterToggle(
                "knockback_link", "KnockbackLink",
                false,
                "Toggles KnockbackLink."
                );
            damageLink = page.RegisterToggle(
                "damage_link", "DamageLink",
                false,
                "Toggles DamageLink."
                );
            teleportToStart = page.RegisterButton(
                "teleport_to_start", "Teleport to start",
                description: "Teleports you back to your starting room."
                );
        }

        /// <summary>
        /// Runs a button's action whenever it is pressed.
        ///
        /// runInitial defaults to false, unlike the toggle and number
        /// overloads. Those apply a stored value, and applying it once at
        /// startup is the whole point; a button has no value, so running it at
        /// startup is pressing it on the player's behalf. That is what warped
        /// everyone to their starting room on the first save load of a session:
        /// the teleport button fired during CreateOptionBindings and left
        /// queuedWarp armed.
        /// </summary>
        internal static void Bind(ButtonOption button, Action apply, bool runInitial = false)
        {
            if (runInitial) apply();
            button.Pressed += apply;
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

        internal static void CreateOptionBindings(GuiManager guiManager, ArchipelagoManager apManager)
        {
#pragma warning disable CS8604 // Possible null reference argument.
            guiManager.SetToggleKeybind(menuToggle);
            Bind(toastDuration, guiManager.SetToastDuration);
            Bind(deathLink, apManager.ToggleDeathLink);
            Bind(knockbackLink, apManager.ToggleKnockbackLink);
            Bind(damageLink, apManager.ToggleDamageLink);
            Bind(teleportToStart, () => apManager.queuedWarp = true, runInitial: false);
#pragma warning restore CS8604 // Possible null reference argument.
        }
    }
}