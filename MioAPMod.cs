using MioModLoader;
namespace MioAP
{
    /// <summary>
    /// The entrypoint for our mod. Initializes all of our managers, wires them together, and initializes their hooks.
    /// </summary>
    public class MioAPMod : Mod
    {
        DataManager? dataManager;
        ArchipelagoManager? apManager;
        HooksManager? hooksManager;
        GuiManager? guiManager;

        public override unsafe void Initialize()
        {
            dataManager = new DataManager(LogMessage);
            apManager = new ArchipelagoManager(LogMessage, dataManager);
            guiManager = new GuiManager(LogMessage, apManager);
            hooksManager = new HooksManager(LogMessage, dataManager, apManager, guiManager.ShowToast);

            hooksManager.InitHooks();
            guiManager.InitHooks();

            LogMessage("Initialization complete");
        }
    }
}
