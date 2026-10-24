using MioModdingApi;

namespace MioAP
{
    internal unsafe class MenuManager
    {
        // ---- dependencies ----
        private readonly Action<string> _loggingCBMethod;
        private readonly ArchipelagoManager apManager;
        private readonly GuiManager guiManager;

        // ---- page elements ----
        ModMenuPage? menuPage;

        public NumberOption? toastSecondsOp;

        public MenuManager(Action<string> loggingCBMethod, ArchipelagoManager apManager, GuiManager guiManager)
        {
            _loggingCBMethod = loggingCBMethod;
            this.apManager = apManager;
            this.guiManager = guiManager;

            SetupMenu();
        }

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        private void SetupMenu()
        {
            try { menuPage = ModMenu.RegisterMod("MIO AP", "Mod for the Archipelago Randomizer"); }
            catch (Exception ex) { LogMessage("[AP] Could not register mod menu: " + ex); }

            if (menuPage == null) return;
            try
            {
                toastSecondsOp = menuPage.RegisterNumber(
                    "toast_seconds", "Message time", 
                    1, 20, 1, 5, 
                    "The amount of seconds that archipelago message popups will persist.");
                toastSecondsOp.Changed += (int value) => guiManager.toastSeconds = value;
            }
            catch (Exception ex) { LogMessage("[AP] Could not register menu options: " + ex); }
        }
    }
}
