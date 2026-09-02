using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using MioGame;
using MioModdingApi;
using MioModLoader;
using Newtonsoft.Json;
using PolyHook2.API;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace MioAP
{
    public class MioAPMod : Mod
    {
        DataManager? dataManager;
        ArchipelagoManager? apManager;
        HooksManager? hooksManager;
        GuiManager? guiManager;

        public override unsafe void Initialize()
        {
            //Hooks();

            //Example .gin Patch
            //GinPatching.PatchGins += () =>
            //{
            //GinPatching.AddGinPatch("flamby/assets.gin", Path.Combine(GetModFolderPath(), "assets_override.gin"));
            //};

            dataManager = new DataManager(LogMessage);

            apManager = new ArchipelagoManager(LogMessage, dataManager);

            hooksManager = new HooksManager(LogMessage, dataManager, apManager);

            guiManager = new GuiManager(LogMessage, apManager);

            hooksManager.InitHooks();

            guiManager.InitHooks();

            On.MioGame.GlobalFunctions.platform.win32.On_entrypoint.main.Prefix += Main_Prefix;

            LogMessage("Initialization complete");
        }

        private unsafe void Main_Prefix(int argc, sbyte** argv, sbyte** envp)
        { }

        //private unsafe void Hooks()
        //{
        //Example Hook
        //On.MioGame.On_Game.fixed_update.Prefix += Fixed_update_Prefix;
        //}

        //private unsafe void Fixed_update_Prefix(MioGame.Game* __this)
        //{
        //var mio = __this->mio;
        //if (mio.node != null && !mio.cutscene.active && !mio.walk_bot.active && mio.hook.state._value == MioGame.Mio.Hook.State.Inactive)
        //{
        //mio.move_by_slide(new MioGame.Vec_float_3() { Base = new MioGame._vec_storage_float_3() { x = 0.1f } });
        //}
        //}
    }
}
