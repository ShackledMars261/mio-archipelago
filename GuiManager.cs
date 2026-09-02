using MioGame;
using System.Runtime.InteropServices;
using System.Text;
using static MioGame.GlobalFunctions.engine.gui;
using static MioGame.GlobalFunctions.Functions;

namespace MioAP
{
    internal unsafe class GuiManager
    {
        // ---- ImGui enum values ----
        private const int Cond_FirstUseEver = 1 << 2;    // 4
        private const int Cond_Always = 1 << 0;    // 1
        private const int WindowFlags_NoDocking = 1 << 21;   // 2097152
        private const int InputTextFlags_CharsDecimal = 1 << 0;    // 1
        private const int InputTextFlags_Password = 1 << 15;   // 32768

        // ---- win32 ----
        private const int VK_INSERT = 0x2D;
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        // ---- dependencies ----
        private readonly Action<string> _loggingCBMethod;
        private readonly ArchipelagoManager apManager;

        // ---- state ----
        private bool _isOpen;          // menu starts closed; INSERT opens it
        private bool _insertWasDown;
        private bool _pollPrimed;

        // ---- connection fields ----
        private readonly byte[] _host = NewBuf("127.0.0.1", 64);
        private readonly byte[] _port = NewBuf("38281", 8);
        private readonly byte[] _slot = NewBuf("shackled", 64);
        private readonly byte[] _password = NewBuf("", 64);

        public GuiManager(Action<string> loggingCBMethod, ArchipelagoManager apManager)
        {
            _loggingCBMethod = loggingCBMethod;
            this.apManager = apManager;
        }

        private void LogMessage(string message) => _loggingCBMethod?.Invoke(message);

        public void InitHooks()
        {
            On.MioGame.GlobalFunctions.engine.On_gui.gui_render.Prefix += Gui_render_Prefix;
            LogMessage("[gui] hooks installed");
        }

        /// <summary>
        /// Runs every frame, between the game's NewFrame and its Render.
        /// </summary>
        private void Gui_render_Prefix()
        {
            try
            {
                PollToggle();
                UpdateCursor();

                if (_isOpen) Draw();
            }
            catch (Exception ex)
            {
                // Runs on the game's render thread; never let this escape.
                LogMessage("[gui] draw error: " + ex);
            }
        }

        private void PollToggle()
        {
            bool down = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;

            // First frame: record the key state without acting on it, so a key
            // already held at startup doesn't register as a fresh press.
            if (!_pollPrimed)
            {
                _pollPrimed = true;
                _insertWasDown = down;
                return;
            }

            if (down && !_insertWasDown) _isOpen = !_isOpen;
            _insertWasDown = down;
        }

        /// <summary>
        /// The game re-asserts cursor visibility every frame, so this must be
        /// called every frame rather than only on change.
        /// </summary>
        private void UpdateCursor()
        {
            try { toggle_mouse_cursor(_isOpen); }
            catch (Exception ex) { LogMessage("[gui] cursor toggle failed: " + ex); }
        }

        private void Draw()
        {
            // The devs added a custom kill switch to their ImGui fork. While
            // gui_disabled is set, Begin() marks every window Hidden and returns
            // false. Clear it for our window, then restore it so the game's own
            // F11 logic stays in sync.
            bool wasDisabled = ImGui__is_gui_disabled();
            if (wasDisabled) ImGui__enable_gui();

            try
            {
                var pos = new ImVec2 { x = 12f, y = 12f };
                var pivot = new ImVec2 { x = 0f, y = 0f };
                var size = new ImVec2 { x = 260f, y = 220f };

                ImGui__SetNextWindowPos(&pos, Cond_FirstUseEver, &pivot);
                ImGui__SetNextWindowSize(&size, Cond_FirstUseEver);
                ImGui__SetNextWindowCollapsed(false, Cond_Always);
                ImGui__SetNextWindowBgAlpha(0.92f);

                fixed (byte* title = Utf8("Archipelago"))
                {
                    bool open = _isOpen;

                    if (ImGui__Begin((sbyte*)title, &open, WindowFlags_NoDocking))
                    {
                        ImGui__PushItemWidth(180f);
                        InputText("Host", _host, 0);
                        InputText("Port", _port, InputTextFlags_CharsDecimal);
                        InputText("Slot name", _slot, 0);
                        InputText("Password", _password, InputTextFlags_Password);
                        ImGui__PopItemWidth();

                        ImGui__Separator();

                        string host = FromBuf(_host);
                        string slot = FromBuf(_slot);
                        string portStr = FromBuf(_port);

                        bool valid = host.Length > 0 && slot.Length > 0
                                  && int.TryParse(portStr, out int p) && p > 0 && p < 65536;

                        ImGui__BeginDisabled(!valid);
                        if (Button("Connect", 120f))
                        {
                            try
                            {
                                apManager.Connect(host, int.Parse(portStr), slot, FromBuf(_password));
                            }
                            catch (Exception ex)
                            {
                                LogMessage("[gui] connect failed: " + ex);
                            }
                        }
                        ImGui__EndDisabled();

                        ImGui__SameLine(0f, -1f);
                        if (Button("Close", 120f)) open = false;

                        ImGui__Separator();
                        Text("INSERT = toggle menu");
                    }
                    ImGui__End();

                    _isOpen = open;
                }
            }
            finally
            {
                if (wasDisabled) ImGui__disable_gui();
            }
        }

        // ---- helpers over the raw bindings ----

        private static byte[] NewBuf(string initial, int size)
        {
            var buf = new byte[size];
            byte[] src = Encoding.UTF8.GetBytes(initial);
            Array.Copy(src, buf, Math.Min(src.Length, size - 1));
            return buf;
        }

        private static byte[] Utf8(string s)
        {
            byte[] b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
            return b;   // trailing 0 from array init
        }

        private static string FromBuf(byte[] buf)
        {
            int len = Array.IndexOf<byte>(buf, 0);
            if (len < 0) len = buf.Length;
            return Encoding.UTF8.GetString(buf, 0, len);
        }

        private static void InputText(string label, byte[] buf, int flags)
        {
            fixed (byte* l = Utf8(label))
            fixed (byte* b = buf)
                ImGui__InputText((sbyte*)l, (sbyte*)b, (ulong)buf.Length, flags, default, null);
        }

        private static bool Button(string label, float width)
        {
            var size = new ImVec2 { x = width, y = 0f };
            fixed (byte* l = Utf8(label))
                return ImGui__Button((sbyte*)l, &size);
        }

        private static void Text(string s)
        {
            fixed (byte* t = Utf8(s))
                ImGui__TextUnformatted((sbyte*)t, null);
        }
    }
}