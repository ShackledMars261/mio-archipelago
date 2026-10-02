using MioGame;
using System;

namespace MioAP
{
    /// <summary>
    /// Keeps Archipelago and the player's save slots paired up: each slot
    /// remembers the connection that was last used with it, so returning to a
    /// multiworld is just "load save", and being in the world implies being
    /// connected.
    ///
    /// WHY IT DOESN'T REFUSE THE TRANSITION. The obvious design - block the
    /// menu-to-game transition until connected - doesn't work here. The menu
    /// commits when the button is pressed: the fade starts immediately and
    /// Metagame::go_to_game runs at the END of it. Suppressing go_to_game
    /// leaves the state machine in a combination update_transitions has no case
    /// for and the game aborts; cancelling Main_menu::act after the fact stops
    /// the destination but not the fade, stranding the player on black. There
    /// is no cancel path.
    ///
    /// SO THE LOAD IS THE CONNECTION WINDOW. The connect starts at go_to_game
    /// and runs on its own thread while the game streams zones - several
    /// seconds, far more than a handshake needs - so in practice the session is
    /// live well before the world is. If it isn't, the player is returned to
    /// the menu through go_to_main_menu, which is a transition the game already
    /// supports.
    ///
    /// DETECT AND ACT ARE SPLIT. The check that we're in the world without a
    /// session happens in Game::fixed_update, but the bounce itself can't: it
    /// destroys the Game object whose update is still on the stack. So the
    /// prefix only raises a flag and Metagame::update, which runs outside that
    /// stack, performs it.
    ///
    /// WHERE THE DETAILS LIVE. In a sidecar file, not the .save. go_to_game
    /// fires long before Game::load_save reads anything, so at the only point
    /// where we can act on the details, the save isn't in memory yet.
    ///
    /// Both "Continue" and "Load game" arrive as Action.Load_game, so there is
    /// one path for resuming and one for New_game.
    ///
    /// Only the gate's own outcomes are announced here - a mismatched save, a
    /// bounce. A successful connection is announced by ArchipelagoManager, so
    /// it reads the same whether the player typed it or the gate restored it.
    /// </summary>
    internal sealed class SaveGate
    {
        private readonly Action<string> _loggingCBMethod;
        private readonly ArchipelagoManager apManager;
        private readonly ConnectionStore connectionStore;
        private readonly GuiManager guiManager;

        /// <summary>
        /// Set once the bounce has been carried out, so the condition isn't
        /// detected again every frame on the way back. Re-armed on returning to
        /// the menu, so a second attempt is checked too.
        /// </summary>
        private bool _bounced;

        /// <summary>
        /// Carries the bounce from the stack that detects it to the one that can
        /// safely perform it: set in Game::fixed_update, consumed in
        /// Metagame::update. Also suppresses re-detection in the frames between
        /// the two.
        /// </summary>
        private bool _bounceRequested;

        public SaveGate(
            Action<string> loggingCBMethod,
            ArchipelagoManager apManager,
            ConnectionStore connectionStore,
            GuiManager guiManager)
        {
            _loggingCBMethod = loggingCBMethod;
            this.apManager = apManager;
            this.connectionStore = connectionStore;
            this.guiManager = guiManager;
        }

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        public unsafe void InitHooks()
        {
            // Start the connection as the load begins.
            On.MioGame.On_Metagame.go_to_game.Hook += Go_to_game_Hook;

            // Detect a disconnected run, then act on it from a safe stack.
            On.MioGame.On_Game.fixed_update.Prefix += Enforce_connected_Prefix;
            On.MioGame.On_Metagame.update.Suffix += Metagame_update_Suffix;

            // Bind the slot to the connection once the save id is known.
            On.MioGame.On_Game.load_save.Suffix += Load_save_Suffix;

            LogMessage("[gate] hooks installed");
        }

        // ---------------------------------------------------------------
        // Start connecting as the load begins
        // ---------------------------------------------------------------

        /// <summary>
        /// Kicks off the connection for a resumed save. Always lets the
        /// transition through - refusing it is what broke the state machine.
        /// </summary>
        private unsafe void Go_to_game_Hook(
            On.MioGame.On_Metagame.orig_go_to_game orig,
            Metagame* self,
            Metagame.Main_menu.Action action)
        {
            try
            {
                uint slot = self->save_slot_index;
                LogMessage($"[gate] go_to_game slot={slot} action={action} " +
                           $"ap={apManager.State}");

                if (!apManager.IsConnected && action == Metagame.Main_menu.Action.Load_game)
                {
                    var creds = connectionStore.ForSaveSlot(slot);
                    if (creds != null)
                    {
                        LogMessage($"[gate] connecting to {creds} during load of slot {slot}");
                        apManager.Connect(creds.Host, creds.Port, creds.Slot, creds.Password);
                    }
                    else
                    {
                        LogMessage($"[gate] no stored connection for slot {slot}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage("[gate] go_to_game failed: " + ex);
            }

            orig(self, action);
        }

        // ---------------------------------------------------------------
        // Detect a disconnected run
        // ---------------------------------------------------------------

        /// <summary>
        /// In the world without a session means something went wrong - a failed
        /// connect, or a new game the player never connected for. Queues the
        /// return to the menu rather than let a disconnected run accumulate
        /// progress that will never reach the server.
        ///
        /// Only flags it: the bounce itself happens in Metagame_update_Suffix,
        /// for the reason documented there.
        /// </summary>
        private unsafe void Enforce_connected_Prefix(Game* __this)
        {
            try
            {
                ref Metagame meta = ref MioGame.Globals.metagame;

                if (meta.state != Metagame.State.Game)
                {
                    _bounced = false;   // back at the menu; arm for the next attempt
                    return;
                }

                if (_bounced || _bounceRequested) return;
                if (apManager.IsConnected) return;
                if (apManager.State == ApState.Connecting) return;   // still trying

                _bounceRequested = true;
                LogMessage($"[gate] in-game without a session ({apManager.State}), queuing bounce");
            }
            catch (Exception ex)
            {
                LogMessage("[gate] enforce failed: " + ex);
            }
        }

        // ---------------------------------------------------------------
        // Act on it
        // ---------------------------------------------------------------

        /// <summary>
        /// Performs the queued bounce.
        ///
        /// It lives here rather than where it is detected because
        /// go_to_main_menu tears down the Game object: called from inside
        /// Game::fixed_update it frees the object whose update is still on the
        /// stack, and the hook dispatcher then invokes orig on it - an access
        /// violation. Metagame::update is outside that stack, so the teardown
        /// has nothing live above it.
        /// </summary>
        private unsafe void Metagame_update_Suffix(Metagame* self)
        {
            try
            {
                if (!_bounceRequested) return;

                _bounceRequested = false;
                _bounced = true;

                string why = apManager.State == ApState.Failed
                    ? "Connection failed: " + apManager.LastError
                    : "Not connected to Archipelago";

                guiManager.ShowToast(why + " - returning to menu");
                LogMessage("[gate] bouncing to menu");

                self->go_to_main_menu();
            }
            catch (Exception ex)
            {
                LogMessage("[gate] update suffix failed: " + ex);
            }
        }

        // ---------------------------------------------------------------
        // Pair the save with its connection
        // ---------------------------------------------------------------

        /// <summary>
        /// Once a save is in memory its id is known, so this is where a slot and
        /// a connection get bound together - and where a mismatch is caught.
        /// </summary>
        private unsafe void Load_save_Suffix(Game* __this)
        {
            try
            {
                if (!apManager.IsConnected) return;

                ref Game game = ref MioGame.Globals.game;
                ref Metagame meta = ref MioGame.Globals.metagame;

                uint slot = meta.save_slot_index;
                ulong saveId = game.save.id;

                var stored = connectionStore.ForSaveSlot(slot);

                // A different save now occupies this slot, so the stored details
                // belong to a multiworld this file was never part of. This can
                // only be noticed after loading, since the id lives in the save.
                if (stored != null && stored.SaveId != 0 && stored.SaveId != saveId)
                {
                    LogMessage($"[gate] slot {slot} now holds save {saveId}, but stored " +
                               $"details were for {stored.SaveId} - clearing them");

                    guiManager.ShowToast("This save doesn't match the stored Archipelago room");
                    connectionStore.Forget(slot);
                    return;
                }

                var live = apManager.CurrentDetails;
                if (live == null) return;

                live.SaveId = saveId;
                connectionStore.Remember(slot, live);
            }
            catch (Exception ex)
            {
                LogMessage("[gate] load_save suffix failed: " + ex);
            }
        }
    }
}