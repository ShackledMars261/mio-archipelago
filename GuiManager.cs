using MioGame;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static MioGame.GlobalFunctions.engine.gui;
using static MioGame.GlobalFunctions.Functions;

namespace MioAP
{
    /// <summary>
    /// The in-game Archipelago UI: a connection menu toggled with INSERT, and
    /// transient notifications when items arrive.
    ///
    /// This draws into the game's OWN ImGui context rather than compositing a
    /// separate overlay, by hooking gui_render and submitting widgets between the
    /// game's NewFrame and Render. That means no second D3D device, no native
    /// cimgui dependency, and it works in exclusive fullscreen.
    ///
    /// The catch is that the developers patched their ImGui with a global kill
    /// switch, gui_disabled, which is set whenever their debug menu is off. While
    /// it's set, Begin() marks every window Hidden and returns false, so our
    /// windows never render. Draw clears the flag around our own submissions and
    /// restores it immediately, leaving the game's F11 toggle in sync.
    ///
    /// Two smaller consequences of living inside someone else's context:
    /// the INSERT toggle polls GetAsyncKeyState rather than reading ImGui's key
    /// state, because ImGui only sees input when one of its windows has focus; and
    /// the cursor has to be re-asserted every frame, because the game sets it back
    /// every frame.
    ///
    /// ShowToast is safe to call from any thread - messages cross to the render
    /// thread through a queue, since items are granted during fixed_update.
    /// </summary>
    internal unsafe class GuiManager
    {
        // ---- ImGui enum values ----
        private const int Cond_FirstUseEver = 1 << 2;    // 4
        private const int Cond_Always = 1 << 0;    // 1
        private const int InputTextFlags_CharsDecimal = 1 << 0;    // 1
        private const int InputTextFlags_Password = 1 << 15;   // 32768
        private const int Col_Text = 0;

        private const int WindowFlags_NoTitleBar = 1 << 0;
        private const int WindowFlags_NoResize = 1 << 1;
        private const int WindowFlags_NoMove = 1 << 2;
        private const int WindowFlags_NoScrollbar = 1 << 3;
        private const int WindowFlags_AlwaysAutoResize = 1 << 6;
        private const int WindowFlags_NoSavedSettings = 1 << 8;
        private const int WindowFlags_NoMouseInputs = 1 << 9;
        private const int WindowFlags_NoFocusOnAppearing = 1 << 12;
        private const int WindowFlags_NoBringToFrontOnFocus = 1 << 13;
        private const int WindowFlags_NoNavInputs = 1 << 18;
        private const int WindowFlags_NoNavFocus = 1 << 19;
        private const int WindowFlags_NoDocking = 1 << 21;

        /// <summary>A passive overlay: no chrome, no input, never steals focus.</summary>
        private const int ToastWindowFlags =
            WindowFlags_NoTitleBar | WindowFlags_NoResize | WindowFlags_NoMove |
            WindowFlags_NoScrollbar | WindowFlags_AlwaysAutoResize |
            WindowFlags_NoSavedSettings | WindowFlags_NoMouseInputs |
            WindowFlags_NoFocusOnAppearing | WindowFlags_NoBringToFrontOnFocus |
            WindowFlags_NoNavInputs | WindowFlags_NoNavFocus | WindowFlags_NoDocking;

        // ---- toast tuning ----
        private const double ToastSeconds = 5.0;      // total lifetime
        private const double ToastFadeSeconds = 1.0;  // fade-out at the end of it
        private const int MaxToasts = 5;
        private const float ToastMargin = 16f;

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

        // Handoff from whichever thread grants items to the render thread.
        private readonly ConcurrentQueue<string> _incomingToasts = new();
        private readonly List<(string text, double born)> _toasts = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // ---- connection fields ----
        private readonly byte[] _host = NewBuf("127.0.0.1", 64);
        private readonly byte[] _port = NewBuf("38281", 8);
        private readonly byte[] _slot = NewBuf("", 64);
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
        /// Queues an on-screen notification. Safe to call from any thread; the
        /// message is picked up on the next rendered frame.
        /// </summary>
        public void ShowToast(string message)
        {
            if (!string.IsNullOrEmpty(message)) _incomingToasts.Enqueue(message);
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
                PumpToasts();

                if (!_isOpen && _toasts.Count == 0) return;

                // The devs added a custom kill switch to their ImGui fork. While
                // gui_disabled is set, Begin() marks every window Hidden and
                // returns false. Clear it for our windows, then restore it so the
                // game's own F11 logic stays in sync.
                bool wasDisabled = ImGui__is_gui_disabled();
                if (wasDisabled) ImGui__enable_gui();

                try
                {
                    if (_isOpen) DrawMenu();
                    if (_toasts.Count > 0) DrawToasts();
                }
                finally
                {
                    if (wasDisabled) ImGui__disable_gui();
                }
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

        // ===============================================================
        // Toasts
        // ===============================================================

        /// <summary>Moves queued messages into the live list and retires expired ones.</summary>
        private void PumpToasts()
        {
            while (_incomingToasts.TryDequeue(out string? text))
            {
                _toasts.Add((text, _clock.Elapsed.TotalSeconds));

                // Oldest first, so trimming from the front drops the stalest.
                if (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);
            }

            if (_toasts.Count == 0) return;

            double now = _clock.Elapsed.TotalSeconds;
            _toasts.RemoveAll(t => now - t.born >= ToastSeconds);
        }

        private void DrawToasts()
        {
            ImGuiIO* io = ImGui__GetIO();

            // Top-right, pinned by its own top-right corner so the box grows
            // leftward and downward as messages stack up.
            var pos = new ImVec2 { x = io->DisplaySize.x - ToastMargin, y = ToastMargin };
            var pivot = new ImVec2 { x = 1f, y = 0f };

            ImGui__SetNextWindowPos(&pos, Cond_Always, &pivot);
            ImGui__SetNextWindowBgAlpha(0.70f);

            // "##" keeps the id but draws no title - the window has no title bar.
            fixed (byte* title = Utf8("##ap_toasts"))
            {
                if (ImGui__Begin((sbyte*)title, null, ToastWindowFlags))
                {
                    double now = _clock.Elapsed.TotalSeconds;

                    foreach (var (text, born) in _toasts)
                    {
                        double age = now - born;
                        double fadeStart = ToastSeconds - ToastFadeSeconds;

                        float alpha = age <= fadeStart
                            ? 1f
                            : (float)((ToastSeconds - age) / ToastFadeSeconds);

                        if (alpha < 0f) alpha = 0f;

                        var col = new ImVec4 { x = 1f, y = 1f, z = 1f, w = alpha };
                        ImGui__PushStyleColor(Col_Text, &col);
                        Text(text);
                        ImGui__PopStyleColor(1);
                    }
                }
                ImGui__End();
            }
        }

        // ===============================================================
        // Connection menu
        // ===============================================================

        private void DrawMenu()
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

                    ImGui__BeginDisabled(!valid || apManager.IsConnected);
                    if (Button(apManager.IsConnected ? "Connected" : "Connect", 120f))
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

        // ===============================================================
        // Helpers over the raw bindings
        // ===============================================================

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