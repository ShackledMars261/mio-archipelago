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
        ConnectionStore? connectionStore;
        GuiManager? guiManager;
        HooksManager? hooksManager;
        SaveGate? saveGate;

        internal static MioAPMod? Instance { get; private set; }

        public override unsafe void Initialize()
        {
            Instance = this;

            ModOptions.Register(this);

            dataManager = new DataManager(LogMessage);
            apManager = new ArchipelagoManager(LogMessage, dataManager);
            connectionStore = new ConnectionStore(LogMessage);
            guiManager = new GuiManager(LogMessage, apManager);
            apManager.SetToastSink(guiManager.ShowToast);
            hooksManager = new HooksManager(LogMessage, dataManager, apManager, guiManager.ShowToast);
            saveGate = new SaveGate(LogMessage, apManager, connectionStore, guiManager);

            hooksManager.InitHooks();
            guiManager.InitHooks();
            saveGate.InitHooks();

            ModOptions.CreateOptionBindings(guiManager);

            LogMessage("Initialization complete");
        }
    }
}
