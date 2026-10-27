using MioGame;
using MioModdingApi;
using System.Runtime.InteropServices;

namespace MioAP
{
    /// <summary>
    /// Edge-detects presses of a <see cref="KeybindOption"/> from the render thread, where the
    /// connection menu lives.
    ///
    /// Keyboard and mouse go through GetAsyncKeyState rather than the game's input state, for the
    /// same reason the INSERT toggle always did: it works at the main menu, and while one of our ImGui
    /// windows has focus. The game's Key values are scan codes (0x100 = extended), so they're mapped
    /// to virtual keys with MapVirtualKey. A controller button is read from the game's pad state.
    /// </summary>
    internal sealed unsafe class MenuKeyPoller
    {
        private const uint MAPVK_VK_TO_CHAR = 2;
        private const uint MAPVK_VSC_TO_VK_EX = 3;
        private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private readonly KeybindOption keybind;

        private bool wasDown;
        private bool primed;
        private (Key, Mouse_button, Gamepad_button) polledBinding;

        public MenuKeyPoller(KeybindOption keybind)
        {
            this.keybind = keybind;
        }

        /// <summary>
        /// True on the frame the binding is pressed. Call once per frame.
        /// </summary>
        /// <param name="typing">
        /// True while an ImGui text field has focus: a key that types a character is then ignored,
        /// so binding the menu to e.g. K doesn't close it while typing a slot name.
        /// </param>
        public bool Pressed(bool typing)
        {
            var binding = (keybind.Key, keybind.MouseButton, keybind.Button);
            bool down = IsDown(typing);

            // First frame, or just rebound: record without acting, so a key that's already held
            // (including the one just pressed to rebind) isn't taken as a fresh press.
            if (!primed || binding != polledBinding)
            {
                primed = true;
                polledBinding = binding;
                wasDown = down;
                return false;
            }

            bool pressed = down && !wasDown;
            wasDown = down;
            return pressed;
        }

        /// <summary>The bound key's display name, e.g. "INSERT", or null if no key or mouse button is bound.</summary>
        public string? KeyName()
        {
            if (keybind.Key != Key.Key_dummy) return keybind.Key.ToString().Replace("Key_", "").ToUpperInvariant();
            if (keybind.MouseButton != Mouse_button.Mouse_none) return keybind.MouseButton.ToString().Replace("_", " ");
            return null;
        }

        private bool IsDown(bool typing)
        {
            int vk = KeyToVk(keybind.Key);
            if (vk != 0 && !(typing && TypesCharacter(vk)) && (GetAsyncKeyState(vk) & 0x8000) != 0) return true;

            vk = MouseToVk(keybind.MouseButton);
            if (vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0) return true;

            Gamepad_button button = keybind.Button;
            if (button != Gamepad_button.Btn_none && (uint)button < Fixed_array_Input_btn_22.count)
            {
                return MioGame.Globals.input.game.pad.buttons.data[(int)button].down;
            }
            return false;
        }

        private static int KeyToVk(Key key)
        {
            uint code = (uint)key;
            if (code == 0) return 0;
            // Scan code with 0x100 for extended keys; MapVirtualKey wants the 0xE0 prefix instead.
            uint scan = (code & 0xFF) | ((code & 0x100) != 0 ? 0xE000u : 0u);
            return (int)MapVirtualKey(scan, MAPVK_VSC_TO_VK_EX);
        }

        private static int MouseToVk(Mouse_button button) => button switch
        {
            Mouse_button.Mouse_left => VK_LBUTTON,
            Mouse_button.Mouse_right => VK_RBUTTON,
            Mouse_button.Mouse_middle => VK_MBUTTON,
            Mouse_button.Mouse_back => VK_XBUTTON1,
            Mouse_button.Mouse_forward => VK_XBUTTON2,
            _ => 0,
        };

        private static bool TypesCharacter(int vk) => MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR) != 0;
    }
}