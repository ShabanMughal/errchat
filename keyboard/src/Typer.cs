// Puts text into the app in front, without taking focus from it.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace ErrChatKeyboard
{
    public static class Typer
    {
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        const uint INPUT_KEYBOARD = 1;
        const uint KEYUP = 0x0002;
        const uint UNICODE = 0x0004;
        const ushort VK_BACK = 0x08, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_A = 0x41, VK_V = 0x56;

        static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            return new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
        }

        static void Send(List<INPUT> inputs)
        {
            if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>A shortcut's Alt or Ctrl may still be down; typing now would arrive as a shortcut.</summary>
        static void WaitForModifiers()
        {
            long end = Environment.TickCount + 1500;
            ushort[] keys = { VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN };
            while (Environment.TickCount < end)
            {
                bool down = false;
                foreach (var k in keys) if ((GetAsyncKeyState(k) & 0x8000) != 0) down = true;
                if (!down) return;
                Thread.Sleep(15);
            }
        }

        static void Combo(ushort modifier, ushort key)
        {
            Send(new List<INPUT> { Key(modifier, 0, 0), Key(key, 0, 0), Key(key, 0, KEYUP), Key(modifier, 0, KEYUP) });
        }

        /// <summary>Removes the partial word, then types the replacement. Doesn't touch the clipboard.</summary>
        public static void TypeWord(int backspaces, string text, IntPtr window)
        {
            WaitForModifiers();
            if (GetForegroundWindow() != window) return;
            var inputs = new List<INPUT>();
            for (int i = 0; i < backspaces; i++) { inputs.Add(Key(VK_BACK, 0, 0)); inputs.Add(Key(VK_BACK, 0, KEYUP)); }
            AddText(inputs, text);
            Send(inputs);
        }

        static void AddText(List<INPUT> inputs, string text)
        {
            // Sent as UTF-16 units, so emoji arrive as their two surrogate halves.
            foreach (char c in text)
            {
                inputs.Add(Key(0, c, UNICODE));
                inputs.Add(Key(0, c, UNICODE | KEYUP));
            }
        }

        /// <summary>Replaces the whole checked text. Returns false if the app in front changed.</summary>
        public static bool Replace(TextPatternRange range, string text, IntPtr window)
        {
            WaitForModifiers();
            if (GetForegroundWindow() != window) return false;
            bool selected = false;
            if (range != null)
            {
                try { range.Select(); selected = true; }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
            if (!selected) Combo(VK_CONTROL, VK_A);
            Thread.Sleep(30);

            if (text.IndexOf('\n') < 0)
            {
                var inputs = new List<INPUT>();
                AddText(inputs, text);
                Send(inputs);
                return true;
            }
            // Typing a newline would press Enter and send the message, so paste instead.
            Paste(text);
            return true;
        }

        static void Paste(string text)
        {
            string saved = null;
            RunOnSta(() =>
            {
                if (Clipboard.ContainsText()) saved = Clipboard.GetText();
                Clipboard.SetText(text);
            });
            Combo(VK_CONTROL, VK_V);
            Thread.Sleep(400); // give the app time to read it
            RunOnSta(() =>
            {
                if (saved != null) Clipboard.SetText(saved);
                else Clipboard.Clear();
            });
        }

        static void RunOnSta(Action action)
        {
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (ExternalException) { } // another app holds the clipboard
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(2000);
        }
    }
}
