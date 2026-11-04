// The floating suggestion strip. It never takes focus, so clicking a chip leaves
// the caret where it is in the app being typed in.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ErrChatKeyboard
{
    public enum ChipKind { Word, Reply, Primary, Quiet, Status, Fix, Close }
    public enum ChipIcon { None, Spark }

    public class Chip
    {
        public string Text;
        /// <summary>The small number showing which Alt shortcut picks this chip.</summary>
        public string Hint;
        /// <summary>Shown on hover, for shortcuts and text that had to be cut short.</summary>
        public string Tip;
        public ChipKind Kind;
        public ChipIcon Icon;
        /// <summary>For a grammar fix: the corrected text, with changed words marked.</summary>
        public List<Piece> Pieces;
        public Action Click;
        internal RectangleF Bounds;
    }

    public class Theme
    {
        public Color Background, Border, Frame, ChipBack, ChipHover, Text, Muted, Accent, AccentText, OnAccent, AccentHover, Danger;
        public bool Dark;

        /// <summary>Set only when saving previews, to draw a theme other than the system's.</summary>
        public static bool? ForceDark;

        public static Theme Current()
        {
            bool light = true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (value is int) light = (int)value != 0;
                }
            }
            catch (System.Security.SecurityException) { }
            if (ForceDark.HasValue) light = !ForceDark.Value;
            return light
                ? new Theme { Background = Hex("#ffffff"), Border = Hex("#7f8b91"), Frame = Hex("#c9d1d4"), ChipBack = Hex("#eef1f1"), ChipHover = Hex("#dbe3e2"), Text = Hex("#1a2024"), Muted = Hex("#566168"), Accent = Hex("#0f766e"), AccentText = Hex("#0f766e"), OnAccent = Hex("#ffffff"), AccentHover = Hex("#0b5f58"), Danger = Hex("#b42318") }
                : new Theme { Dark = true, Background = Hex("#1c2226"), Border = Hex("#768289"), Frame = Hex("#3f4a50"), ChipBack = Hex("#2b3338"), ChipHover = Hex("#38424a"), Text = Hex("#e8ecee"), Muted = Hex("#a3aeb4"), Accent = Hex("#5eead4"), AccentText = Hex("#5eead4"), OnAccent = Hex("#042f2e"), AccentHover = Hex("#99f6e4"), Danger = Hex("#fda29b") };
        }

        static Color Hex(string s) { return ColorTranslator.FromHtml(s); }
    }

    /// <summary>Windows 11 window styling. Each call does nothing on older Windows.</summary>
    public static class Dwm
    {
        const int DarkMode = 20, Corners = 33, BorderColor = 34, FrameBounds = 9, Round = 2;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        /// <summary>Returns false where Windows can't round the corners, so the caller draws a frame.</summary>
        public static bool RoundCorners(IntPtr hwnd)
        {
            int value = Round;
            return DwmSetWindowAttribute(hwnd, Corners, ref value, 4) == 0;
        }

        public static void SetBorder(IntPtr hwnd, Color color)
        {
            int value = color.R | color.G << 8 | color.B << 16;
            DwmSetWindowAttribute(hwnd, BorderColor, ref value, 4);
        }

        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DarkMode, ref value, 4);
        }

        /// <summary>The window as drawn, without the invisible resize margin.</summary>
        public static Rectangle VisibleBounds(IntPtr hwnd, Rectangle fallback)
        {
            RECT r;
            if (DwmGetWindowAttribute(hwnd, FrameBounds, out r, Marshal.SizeOf(typeof(RECT))) != 0) return fallback;
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        }
    }

    public class Panel : Form
    {
        const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_HOTKEY = 0x312;
        const int MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, CS_DROPSHADOW = 0x20000;
        const int HotkeyWords = 1, HotkeyFix = 10;
        const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0;
        const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        List<Chip> row1 = new List<Chip>();
        List<Chip> row2 = new List<Chip>();
        string note;
        bool noteIsError;
        RectangleF noteBounds;
        Chip hover;
        Theme theme = Theme.Current();
        bool shown;
        bool hotkeysOn;
        bool roundedByWindows;
        string signature = "";
        Rectangle lastAnchor;
        float scale = 1f;
        float spinAngle;
        Font font, smallFont, strongFont;
        readonly ToolTip tip = new ToolTip();
        readonly Timer spinTimer = new Timer { Interval = 50 };

        public Action<int> HotkeyWord;
        public Action HotkeyApplyFix;

        const float FontPx = 14, SmallPx = 12, ChipHeight = 32, Gap = 8, Pad = 8, ChipPad = 12, IconSize = 14, InnerGap = 8, NoteHeight = 20, Radius = 8;
        const int MaxWidth = 720;
        // Native Windows text drawing: unlike GDI+, it falls back to Segoe UI Emoji for emoji.
        const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        // Measuring with the ellipsis flag on returns widths too small for the text.
        const TextFormatFlags MeasureFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        public Panel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            TopMost = true;
            Text = "ErrChat Keyboard";
            SetFonts();
            spinTimer.Tick += (s, e) =>
            {
                spinAngle = (spinAngle + 30) % 360;
                foreach (var chip in laidOut.Where(c => c.Kind == ChipKind.Status)) Invalidate(Rectangle.Ceiling(chip.Bounds));
            };
            var _ = Handle; // create it now so hotkeys and updates work while hidden
            roundedByWindows = Dwm.RoundCorners(Handle);
        }

        void SetFonts()
        {
            if (font != null) font.Dispose();
            if (smallFont != null) smallFont.Dispose();
            if (strongFont != null) strongFont.Dispose();
            font = new Font("Segoe UI", FontPx * scale, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", SmallPx * scale, GraphicsUnit.Pixel);
            strongFont = new Font("Segoe UI Semibold", FontPx * scale, GraphicsUnit.Pixel);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyFix) { if (HotkeyApplyFix != null) HotkeyApplyFix(); }
                else if (HotkeyWord != null) HotkeyWord(id - HotkeyWords);
                return;
            }
            base.WndProc(ref m);
        }

        void SetHotkeys(bool on)
        {
            if (on == hotkeysOn) return;
            hotkeysOn = on;
            for (int i = 0; i < 3; i++)
            {
                if (on) RegisterHotKey(Handle, HotkeyWords + i, MOD_ALT | MOD_NOREPEAT, (int)Keys.D1 + i);
                else UnregisterHotKey(Handle, HotkeyWords + i);
            }
            if (on) RegisterHotKey(Handle, HotkeyFix, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)Keys.F);
            else UnregisterHotKey(Handle, HotkeyFix);
        }

        /// <summary>For the end-to-end test only: logs how the chat was read.</summary>
        public static void TraceChat(string text)
        {
            string path = Environment.GetEnvironmentVariable("ERRCHAT_KB_TRACE");
            if (string.IsNullOrEmpty(path)) return;
            try { System.IO.File.AppendAllText(path + ".chat", text + "\n"); }
            catch (System.IO.IOException) { }
        }

        /// <summary>For the end-to-end test only: reports what the strip shows.</summary>
        static void Trace(string text)
        {
            string path = Environment.GetEnvironmentVariable("ERRCHAT_KB_TRACE");
            if (string.IsNullOrEmpty(path)) return;
            try { System.IO.File.WriteAllText(path, text); }
            catch (System.IO.IOException) { }
        }

        public void HidePanel()
        {
            if (shown) { signature = ""; Trace("(hidden)"); }
            SetHotkeys(false);
            spinTimer.Stop();
            tip.Hide(this);
            if (!shown) return;
            shown = false;
            ShowWindow(Handle, SW_HIDE);
        }

        static string TraceName(Chip chip) { return chip.Kind == ChipKind.Fix ? "Fix: " + chip.Text : chip.Text; }

        /// <summary>Redraws only if something changed, because this runs several times a second.</summary>
        public void ShowChips(List<Chip> first, List<Chip> second, string noteText, bool noteError, Rectangle anchor, Rectangle caret)
        {
            string sig = string.Join("\u0001", first.Concat(second).Select(c => c.Text + "/" + c.Kind + "/" + c.Hint)) + "\u0002" + noteText + noteError;
            bool same = sig == signature && anchor == lastAnchor && shown;
            if (sig != signature) Trace(string.Join(" | ", first.Concat(second).Select(TraceName)));
            row1 = first;
            row2 = second;
            note = noteText;
            noteIsError = noteError;
            if (same) { SyncClicks(); return; }
            signature = sig;
            lastAnchor = anchor;
            theme = Theme.Current();
            Dwm.SetBorder(Handle, theme.Frame);
            uint dpi = GetDpiForWindow(Handle);
            float newScale = dpi > 0 ? dpi / 96f : 1f;
            if (Math.Abs(newScale - scale) > 0.01f) { scale = newScale; SetFonts(); }
            tip.Hide(this);
            LayoutChips(anchor, caret);
            SetHotkeys(true);
            spinTimer.Enabled = laidOut.Any(c => c.Kind == ChipKind.Status);
            Invalidate();
            if (!shown)
            {
                shown = true;
                ShowWindow(Handle, SW_SHOWNOACTIVATE);
            }
        }

        // The rows were rebuilt with new click handlers but the same look; keep the laid-out bounds.
        List<Chip> laidOut = new List<Chip>();
        void SyncClicks()
        {
            var all = row1.Concat(row2).ToList();
            int hovered = hover == null ? -1 : laidOut.IndexOf(hover);
            for (int i = 0; i < all.Count && i < laidOut.Count; i++) all[i].Bounds = laidOut[i].Bounds;
            laidOut = all;
            hover = hovered >= 0 && hovered < all.Count ? all[hovered] : null;
        }

        float TextWidth(Graphics g, Chip chip)
        {
            if (chip.Pieces == null) return TextRenderer.MeasureText(g, chip.Text, font, Size.Empty, MeasureFlags).Width;
            return chip.Pieces.Sum(p => TextRenderer.MeasureText(g, p.Text, p.Changed ? strongFont : font, Size.Empty, MeasureFlags).Width);
        }

        float ChipWidth(Graphics g, Chip chip)
        {
            float w = ChipPad * 2 * scale + TextWidth(g, chip);
            if (chip.Hint != null) w += TextRenderer.MeasureText(g, chip.Hint, smallFont, Size.Empty, MeasureFlags).Width + InnerGap * scale;
            if (chip.Icon != ChipIcon.None || chip.Kind == ChipKind.Status) w += (IconSize + InnerGap) * scale;
            return w;
        }

        void LayoutChips(Rectangle anchor, Rectangle caret)
        {
            using (var g = CreateGraphics())
            {
                float maxWidth = MaxWidth * scale;
                float pad = Pad * scale, gap = Gap * scale, chipH = ChipHeight * scale;
                var close = row1.Concat(row2).FirstOrDefault(c => c.Kind == ChipKind.Close);
                // The close button is pinned to the top right, so the first line stops short of it.
                float closeRoom = close == null ? 0 : chipH + gap;

                float x, y = pad, widest = 0, firstLineEnd = 0;
                bool any = false;
                foreach (var row in new[] { row1, row2 })
                {
                    var flow = row.Where(c => c.Kind != ChipKind.Close).ToList();
                    if (flow.Count == 0) continue;
                    x = pad;
                    foreach (var chip in flow)
                    {
                        float limit = maxWidth - pad - (y == pad ? closeRoom : 0);
                        float w = ChipWidth(g, chip);
                        if (x > pad && x + w > limit) { x = pad; y += chipH + gap; limit = maxWidth - pad; }
                        w = Math.Min(w, limit - pad);
                        chip.Bounds = new RectangleF(x, y, w, chipH);
                        x += w + gap;
                        widest = Math.Max(widest, x - gap);
                        if (y == pad) firstLineEnd = x - gap;
                        any = true;
                    }
                    y += chipH + gap;
                }

                float bottom = any ? y - gap : pad;
                noteBounds = RectangleF.Empty;
                if (!string.IsNullOrEmpty(note))
                {
                    float icon = noteIsError ? (IconSize + InnerGap) * scale : 0;
                    float noteWidth = TextRenderer.MeasureText(g, note, smallFont, Size.Empty, MeasureFlags).Width + icon;
                    if (any)
                    {
                        noteWidth = Math.Min(noteWidth, maxWidth - pad * 2);
                        noteBounds = new RectangleF(pad, bottom + gap / 2, noteWidth, NoteHeight * scale);
                        widest = Math.Max(widest, pad + noteWidth);
                        bottom = noteBounds.Bottom;
                    }
                    else
                    {
                        // No chips: the note shares the top line with the close button.
                        noteWidth = Math.Min(noteWidth, maxWidth - pad * 2 - closeRoom);
                        noteBounds = new RectangleF(pad, pad, noteWidth, chipH);
                        firstLineEnd = pad + noteWidth;
                        widest = firstLineEnd;
                        bottom = pad + chipH;
                    }
                }
                if (close != null) bottom = Math.Max(bottom, pad + chipH);

                int width = (int)Math.Ceiling(Math.Max(widest, firstLineEnd + closeRoom) + pad);
                if (close != null)
                {
                    width = Math.Max(width, (int)Math.Ceiling(pad * 2 + chipH));
                    close.Bounds = new RectangleF(width - pad - chipH, pad, chipH, chipH);
                }
                int h = (int)Math.Ceiling(bottom + pad);

                var area = Screen.FromRectangle(anchor).WorkingArea;
                int left = Math.Max(area.Left + 8, Math.Min(anchor.Left, area.Right - width - 8));
                // Sit just above the box; in a tall box (a document) sit above the caret line instead.
                int reference = anchor.Height > 200 * scale ? caret.Top : anchor.Top;
                int top = reference - h - (int)(8 * scale);
                if (top < area.Top + 8) top = Math.Min((anchor.Height > 200 * scale ? caret.Bottom : anchor.Bottom) + (int)(8 * scale), area.Bottom - h - 8);
                SetWindowPos(Handle, HWND_TOPMOST, left, top, width, h, SWP_NOACTIVATE);
            }
            laidOut = row1.Concat(row2).ToList();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(theme.Background);
            if (!roundedByWindows)
                using (var pen = new Pen(theme.Frame)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            foreach (var chip in laidOut) DrawChip(g, chip);
            DrawNote(g);
        }

        void DrawChip(Graphics g, Chip chip)
        {
            bool hot = chip == hover && chip.Click != null;
            bool ghost = chip.Kind == ChipKind.Quiet || chip.Kind == ChipKind.Status || chip.Kind == ChipKind.Close;
            Color back = chip.Kind == ChipKind.Primary ? (hot ? theme.AccentHover : theme.Accent)
                : hot ? theme.ChipHover
                : ghost ? theme.Background : theme.ChipBack;
            Color fore = chip.Kind == ChipKind.Primary ? theme.OnAccent : ghost ? theme.Muted : theme.Text;

            using (var path = Rounded(chip.Bounds, Radius * scale))
            {
                using (var brush = new SolidBrush(back)) g.FillPath(brush, path);
                // An outline marks a button, as opposed to text that gets inserted.
                if (chip.Kind == ChipKind.Quiet)
                    using (var pen = new Pen(theme.Border, 1)) g.DrawPath(pen, path);
            }

            if (chip.Kind == ChipKind.Close)
            {
                DrawCross(g, chip.Bounds, fore);
                return;
            }

            float x = chip.Bounds.X + ChipPad * scale;
            float right = chip.Bounds.Right - ChipPad * scale;
            if (chip.Hint != null)
            {
                int w = TextRenderer.MeasureText(g, chip.Hint, smallFont, Size.Empty, MeasureFlags).Width;
                TextRenderer.DrawText(g, chip.Hint, smallFont, new Rectangle((int)x, (int)chip.Bounds.Y, w, (int)chip.Bounds.Height), chip.Kind == ChipKind.Primary ? fore : theme.Muted, back, TextFlags);
                x += w + InnerGap * scale;
            }
            if (chip.Kind == ChipKind.Status)
            {
                DrawSpinner(g, x, chip.Bounds);
                x += (IconSize + InnerGap) * scale;
            }
            else if (chip.Icon == ChipIcon.Spark)
            {
                DrawSpark(g, x, chip.Bounds);
                x += (IconSize + InnerGap) * scale;
            }

            if (chip.Pieces == null)
            {
                var rect = new Rectangle((int)x, (int)chip.Bounds.Y, (int)Math.Ceiling(right - x), (int)chip.Bounds.Height);
                TextRenderer.DrawText(g, chip.Text, font, rect, fore, back, TextFlags | TextFormatFlags.EndEllipsis);
                return;
            }
            foreach (var piece in chip.Pieces)
            {
                var pieceFont = piece.Changed ? strongFont : font;
                int w = TextRenderer.MeasureText(g, piece.Text, pieceFont, Size.Empty, MeasureFlags).Width;
                if (x >= right) break;
                var rect = new Rectangle((int)x, (int)chip.Bounds.Y, (int)Math.Min(w, Math.Ceiling(right - x)), (int)chip.Bounds.Height);
                TextRenderer.DrawText(g, piece.Text, pieceFont, rect, piece.Changed ? theme.AccentText : fore, back, TextFlags);
                if (piece.Changed)
                {
                    // Underlined as well as colored, so the change doesn't rely on color alone.
                    float line = chip.Bounds.Bottom - 6 * scale;
                    using (var pen = new Pen(theme.AccentText, Math.Max(1, 2 * scale))) g.DrawLine(pen, rect.Left, line, rect.Right, line);
                }
                x += w;
            }
        }

        void DrawNote(Graphics g)
        {
            if (string.IsNullOrEmpty(note)) return;
            float x = noteBounds.X;
            if (noteIsError)
            {
                DrawWarning(g, x, noteBounds);
                x += (IconSize + InnerGap) * scale;
            }
            var rect = new Rectangle((int)x, (int)noteBounds.Y, (int)Math.Ceiling(noteBounds.Right - x), (int)noteBounds.Height);
            TextRenderer.DrawText(g, note, smallFont, rect, noteIsError ? theme.Danger : theme.Muted, theme.Background, TextFlags | TextFormatFlags.EndEllipsis);
        }

        void DrawCross(Graphics g, RectangleF area, Color color)
        {
            float half = 5 * scale, cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            using (var pen = new Pen(color, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
                g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
            }
        }

        void DrawSpinner(Graphics g, float x, RectangleF area)
        {
            float size = IconSize * scale, inset = 1.5f * scale;
            var box = new RectangleF(x + inset, area.Y + (area.Height - size) / 2 + inset, size - inset * 2, size - inset * 2);
            using (var pen = new Pen(theme.AccentText, 2 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, box, spinAngle, 270);
        }

        void DrawSpark(Graphics g, float x, RectangleF area)
        {
            // A four-pointed star. Drawn rather than an emoji, which Windows renders in one flat color.
            float r = IconSize * scale / 2, inner = r * 0.28f, cx = x + r, cy = area.Y + area.Height / 2;
            var points = new[]
            {
                new PointF(cx, cy - r), new PointF(cx + inner, cy - inner), new PointF(cx + r, cy), new PointF(cx + inner, cy + inner),
                new PointF(cx, cy + r), new PointF(cx - inner, cy + inner), new PointF(cx - r, cy), new PointF(cx - inner, cy - inner)
            };
            using (var brush = new SolidBrush(theme.AccentText)) g.FillPolygon(brush, points);
        }

        void DrawWarning(Graphics g, float x, RectangleF area)
        {
            float size = IconSize * scale, top = area.Y + (area.Height - size) / 2;
            var triangle = new[] { new PointF(x + size / 2, top + scale), new PointF(x + size, top + size - scale), new PointF(x, top + size - scale) };
            using (var brush = new SolidBrush(theme.Danger)) g.FillPolygon(brush, triangle);
            using (var pen = new Pen(theme.Background, Math.Max(1, 1.5f * scale)))
            {
                g.DrawLine(pen, x + size / 2, top + size * 0.4f, x + size / 2, top + size * 0.62f);
                g.DrawLine(pen, x + size / 2, top + size * 0.72f, x + size / 2, top + size * 0.8f);
            }
        }

        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        Chip HitTest(Point p)
        {
            return laidOut.FirstOrDefault(c => c.Click != null && c.Bounds.Contains(p));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var chip = HitTest(e.Location);
            if (chip != hover)
            {
                hover = chip;
                Cursor = chip != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
                tip.Hide(this);
                if (chip != null && chip.Tip != null)
                    tip.Show(chip.Tip, this, (int)chip.Bounds.X, (int)chip.Bounds.Bottom + (int)(4 * scale), 6000);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            tip.Hide(this);
            if (hover != null) { hover = null; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var chip = HitTest(e.Location);
            if (chip != null) chip.Click();
        }
    }
}
