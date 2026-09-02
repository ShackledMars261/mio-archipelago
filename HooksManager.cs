using MioGame;
using MioModdingApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static On.MioGame.On_Field_path;

namespace MioAP
{
    internal class HooksManager
    {
        private readonly Action<string> _loggingCBMethod;

        private DataManager dataManager;
        private ArchipelagoManager apManager;

        public HooksManager(Action<string> loggingCBMethod, DataManager dataManager, ArchipelagoManager apManager)
        {
            _loggingCBMethod = loggingCBMethod;
            this.dataManager = dataManager;
            this.apManager = apManager;
        }

        private void LogMessage(string message)
        {
            _loggingCBMethod?.Invoke(message);
        }

        public unsafe void InitHooks()
        {
            On.MioGame.On_Game.loot_1.Prefix += loot_1_Prefix;
            On.MioGame.On_Game.loot_1.Hook += loot_1_Hook;
            On.MioGame.On_Mio.has.Prefix += mio_has_Prefix;
            On.MioGame.On_Mio.has.Hook += mio_has_Hook;
            On.MioGame.GlobalFunctions.game.On_game.glitch_state_update.Prefix += glitch_state_update_Prefix;
            On.MioGame.GlobalFunctions.game.On_game.glitch_state_update.Hook += glitch_state_update_Hook;
            On.MioGame.GlobalFunctions.core.On_fmt.stringfv.Prefix += stringfv_Prefix;
            On.MioGame.GlobalFunctions.core.On_fmt.stringfv.Hook += stringfv_Hook;
            On.MioGame.On_Ui_loot_popup.start.Prefix += loot_popup_start_Prefix;

            // Hairpin
            On.MioGame.On_GW_tuto_hook.update_GW_tuto_hook.Hook += update_GW_tuto_hook_Hook;
        }

        private unsafe void update_GW_tuto_hook_Hook(On.MioGame.On_GW_tuto_hook.orig_update_GW_tuto_hook orig, GW_tuto_hook* __this)
        {
            //throw new NotImplementedException();
            orig(__this);
        }

        private unsafe void loot_1_Prefix(MioGame.Game* __this, MioGame.String* item_id, int count, MioGame.Loot_flags unused_1)
        {
            LogMessage("LOOT HOOK DETECTED!!!");
            LogMessage($"    item_id: \"{Util.MioStringToString(*item_id)}\"");
            LogMessage($"    count: \"{count}\"");
            LogMessage($"    unused_1 (Loot_flags): \"{unused_1}\"");
        }

        private unsafe MioGame.Save_entry* loot_1_Hook(On.MioGame.On_Game.orig_loot_1 orig, MioGame.Game* __this, MioGame.String* item_id, int count, MioGame.Loot_flags unused_1)
        {
            string id = Util.MioStringToString(*item_id);
            if (!(id == "RESOURCE:PEARL_SHARDS"))
            {
                apManager.SendLocationCheckFromVanillaSaveEntry(id);
            }

            ref Game game = ref MioGame.Globals.game;
            MioGame.String pearlShardsStr = Util.StringToMioString("RESOURCE:PEARL_SHARDS");
            var pearlShardsSlot = game.save.peek(&pearlShardsStr);

            if (!dataManager.CheckItemInItemListBySaveEntry(id))
            {
                return orig(__this, item_id, count, unused_1);
            }
            return pearlShardsSlot;

        }

        private unsafe void mio_has_Prefix(MioGame.Mio* __this, MioGame.String* item_id)
        {
            //LogMessage("HAS HOOK DETECTED!!!");
            //LogMessage($"    item_id: \"{Util.MioStringToString(*item_id)}\"");
        }

        private unsafe bool mio_has_Hook(On.MioGame.On_Mio.orig_has orig, Mio* __this, MioGame.String* item_id)
        {
            string id = Util.MioStringToString(*item_id);
            if (!dataManager.CheckItemInItemListBySaveEntry(id))
            {
                return orig(__this, item_id);
            }
            return apManager.CheckItemInCacheBySaveEntry(id);
        }

        private unsafe void glitch_state_update_Prefix()
        {
            //LogMessage("GLITCH STATE UPDATE HOOK DETECTED!!!");
        }

        private void glitch_state_update_Hook(On.MioGame.GlobalFunctions.game.On_game.orig_glitch_state_update orig)
        {
            ref Game game = ref MioGame.Globals.game;
            string zoneId = Util.MioStringToString(game.current_zone_id);
            //LogMessage($"zoneId: {zoneId} | insideGlitch: {game.glitch._inside}");
            if (zoneId != "GW_intro_jump_P1" && game.glitch._inside)
            {
                game.exit_glitch();
            }
            orig();
        }

        private unsafe void stringfv_Prefix(sbyte* fmt, MioGame.Array_Val_ref* args)
        {
            //LogMessage("STRINGFV HOOK DETECTED!!!");
            //LogMessage($"    fmt: \"{new string(fmt)}\"");
            //LogMessage($"    args: \"{*args}\"");
        }

        private unsafe void stringfv_Hook(On.MioGame.GlobalFunctions.core.On_fmt.orig_stringfv orig, MioGame.String* __return, sbyte* fmt, Array_Val_ref* args)
        {
            throw new NotImplementedException();
        }

        private unsafe void loot_popup_start_Prefix(MioGame.Ui_loot_popup* __this, MioGame.String* item_id)
        {
            LogMessage("LOOT POPUP START HOOK DETECTED!!!");
            LogMessage($"    item_id: \"{Util.MioStringToString(*item_id)}\"");
        }
    }
}
