// The settings window. Unlike the strip, this is a normal window that takes focus.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ErrChatKeyboard
{
    /// <summary>What the window can read and change in the running app.</summary>
    public interface ISettingsHost
    {
        Settings Prefs { get; }
        bool Paused { get; }
        void SetPaused(bool paused);
        void SetProvider(string provider);
        void SavePrefs();
        bool StartsWithWindows { get; }
        void SetStartWithWindows(bool on);
        void ForgetLearnedWords();
        void Quit();
    }

    /// <summary>Text with no inner margin, so its left edge lines up exactly with everything else.</summary>
    class Caption : Control
    {
        const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        public bool AlignRight;

        public Caption()
        {
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        public static int Measure(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text, font, new Size(width, 0), Flags).Height;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, BackColor, AlignRight ? Flags | TextFormatFlags.Right : Flags);
        }
    }

    /// <summary>Base for the drawn controls: takes keyboard focus and shows a focus ring.</summary>
    abstract class DrawnControl : Control
    {
        protected readonly Theme Theme;
        protected readonly float Zoom;

        protected DrawnControl(Theme theme, float scale)
        {
            Theme = theme;
            Zoom = scale;
            BackColor = theme.Background;
            TabStop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }

        protected abstract void Activate();

        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Activate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) { Activate(); e.Handled = true; }
        }

        public void Press() { Activate(); }
    }

    /// <summary>An on/off switch.</summary>
    class Toggle : DrawnControl
    {
        bool on;
        public Action<bool> Changed;

        public Toggle(Theme theme, float scale, bool value, string name) : base(theme, scale)
        {
            on = value;
            AccessibleName = name;
            AccessibleRole = AccessibleRole.CheckButton;
            Size = new Size((int)(44 * scale), (int)(24 * scale));
        }

        public bool On { get { return on; } }

        protected override void Activate()
        {
            on = !on;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            if (Changed != null) Changed(on);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new ToggleAccess(this); }

        class ToggleAccess : ControlAccessibleObject
        {
            readonly Toggle toggle;
            public ToggleAccess(Toggle owner) : base(owner) { toggle = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override AccessibleStates State { get { return base.State | (toggle.On ? AccessibleStates.Checked : AccessibleStates.None); } }
            public override string DefaultAction { get { return toggle.On ? "Turn off" : "Turn on"; } }
            public override void DoDefaultAction() { toggle.Press(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = 3 * Zoom;
            var track = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
            float radius = track.Height / 2;
            using (var path = Panel.Rounded(track, radius))
            {
                if (on) using (var fill = new SolidBrush(Theme.Accent)) g.FillPath(fill, path);
                else using (var pen = new Pen(Theme.Border, Math.Max(1, 1.5f * Zoom))) g.DrawPath(pen, path);
            }
            // The knob's side says on or off, so the state doesn't rely on color alone.
            float knob = track.Height - 8 * Zoom;
            float x = on ? track.Right - knob - 4 * Zoom : track.Left + 4 * Zoom;
            using (var brush = new SolidBrush(on ? Theme.OnAccent : Theme.Muted))
                g.FillEllipse(brush, x, track.Top + 4 * Zoom, knob, knob);
            if (Focused)
                using (var ring = new Pen(Theme.AccentText, Math.Max(1, 1.5f * Zoom)))
                using (var path = Panel.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f))
                    g.DrawPath(ring, path);
        }
    }

    /// <summary>One choice in a group: the provider cards.</summary>
    class Card : DrawnControl
    {
        readonly string title;
        readonly Font titleFont, smallFont;
        string detail = "";
        bool selected, hot;
        public Action Chosen;

        public Card(Theme theme, float scale, string name, Font strong, Font small) : base(theme, scale)
        {
            title = name;
            titleFont = strong;
            smallFont = small;
            AccessibleName = name;
            AccessibleRole = AccessibleRole.RadioButton;
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public string Detail
        {
            get { return detail; }
            set { detail = value; AccessibleDescription = value; Invalidate(); }
        }

        protected override void Activate() { if (Chosen != null) Chosen(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override AccessibleObject CreateAccessibilityInstance() { return new CardAccess(this); }

        class CardAccess : ControlAccessibleObject
        {
            readonly Card card;
            public CardAccess(Card owner) : base(owner) { card = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
            public override AccessibleStates State { get { return base.State | (card.Selected ? AccessibleStates.Checked : AccessibleStates.None); } }
            public override string DefaultAction { get { return "Select"; } }
            public override void DoDefaultAction() { card.Press(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = selected ? 2 * Zoom : 1;
            var box = new RectangleF(w / 2, w / 2, Width - w - 1, Height - w - 1);
            Color back = hot && !selected ? Theme.ChipBack : Theme.Background;
            using (var path = Panel.Rounded(box, 8 * Zoom))
            {
                using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
                using (var pen = new Pen(selected ? Theme.AccentText : Theme.Border, w)) g.DrawPath(pen, path);
            }
            if (Focused)
                using (var ring = new Pen(Theme.AccentText, 1) { DashStyle = DashStyle.Dot })
                using (var path = Panel.Rounded(new RectangleF(box.X + 4 * Zoom, box.Y + 4 * Zoom, box.Width - 8 * Zoom, box.Height - 8 * Zoom), 4 * Zoom))
                    g.DrawPath(ring, path);

            int pad = (int)(12 * Zoom);
            int line = TextRenderer.MeasureText(title, titleFont).Height;
            int small = TextRenderer.MeasureText("A", smallFont).Height;
            int top = (Height - line - small) / 2;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(pad, top, Width - pad * 3, line), Theme.Text, back, flags);
            TextRenderer.DrawText(g, detail, smallFont, new Rectangle(pad, top + line, Width - pad * 3, small), Theme.Muted, back, flags);

            // A radio mark, so the choice doesn't rely on color alone.
            float size = 16 * Zoom;
            var mark = new RectangleF(Width - pad - size, (Height - size) / 2, size, size);
            using (var pen = new Pen(selected ? Theme.AccentText : Theme.Border, Math.Max(1, 1.5f * Zoom))) g.DrawEllipse(pen, mark);
            if (selected)
                using (var dot = new SolidBrush(Theme.AccentText))
                    g.FillEllipse(dot, mark.X + 4 * Zoom, mark.Y + 4 * Zoom, size - 8 * Zoom, size - 8 * Zoom);
        }
    }

    public class SettingsWindow : Form
    {
        const int ContentWidth = 464, Margin24 = 24;
        // The switch draws 3px inside its bounds to leave room for the focus ring.
        const int ToggleInset = 3;
        const int WM_DPICHANGED = 0x02E0;
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        readonly ISettingsHost host;
        readonly bool noActivate;
        Theme theme;
        float scale = 1f;
        Font titleFont, headingFont, bodyFont, strongFont, smallFont;
        Card claudeCard, codexCard;
        Caption providerNote, grammarNote, repliesNote;
        Button signIn;

        public SettingsWindow(ISettingsHost settingsHost, bool showWithoutFocus = false)
        {
            host = settingsHost;
            noActivate = showWithoutFocus;
            Text = "ErrChat Keyboard settings";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            AutoScroll = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (ArgumentException) { }
            var _ = Handle;
            Dwm.RoundCorners(Handle);
            Build();
            SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        }

        protected override bool ShowWithoutActivation { get { return noActivate; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
            }
            base.Dispose(disposing);
        }

        void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || IsDisposed) return;
            bool dark = Theme.Current().Dark;
            if (dark != theme.Dark) BeginInvoke((Action)Build);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                SetWindowPos(Handle, IntPtr.Zero, r.L, r.T, r.R - r.L, r.B - r.T, SWP_NOZORDER | SWP_NOACTIVATE);
                Build();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ActiveControl = null; // no focus ring until the user presses Tab
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (claudeCard != null) UpdateProvider(); // the CLI may have been installed meanwhile
        }

        int S(float px) { return (int)Math.Round(px * scale); }

        void Build()
        {
            SuspendLayout();
            foreach (var control in Controls.Cast<Control>().ToList()) control.Dispose();
            Controls.Clear();
            AutoScrollPosition = Point.Empty;

            theme = Theme.Current();
            uint dpi = GetDpiForWindow(Handle);
            scale = dpi > 0 ? dpi / 96f : 1f;
            foreach (var old in new[] { titleFont, headingFont, bodyFont, strongFont, smallFont }) if (old != null) old.Dispose();
            titleFont = new Font("Segoe UI Semibold", 20 * scale, GraphicsUnit.Pixel);
            headingFont = new Font("Segoe UI Semibold", 16 * scale, GraphicsUnit.Pixel);
            bodyFont = new Font("Segoe UI", 14 * scale, GraphicsUnit.Pixel);
            strongFont = new Font("Segoe UI Semibold", 14 * scale, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
            BackColor = theme.Background;
            ForeColor = theme.Text;
            Dwm.SetDarkTitleBar(Handle, theme.Dark);

            int left = S(Margin24), width = S(ContentWidth);
            int y = S(Margin24);

            // Header, with the master switch.
            var title = AddText("ErrChat Keyboard", titleFont, theme.Text, left, y, width - S(60));
            var enabled = new Toggle(theme, scale, !host.Paused, "Show suggestions");
            enabled.Location = new Point(left + width - enabled.Width + S(ToggleInset), y + S(2));
            enabled.Changed = on => host.SetPaused(!on);
            Controls.Add(enabled);
            y = title.Bottom + S(4);
            y = AddText("Typing help in the apps you already use.", bodyFont, theme.Muted, left, y, width - S(60)).Bottom + S(24);

            // Provider.
            y = AddText("AI provider", headingFont, theme.Text, left, y, width).Bottom + S(12);
            int cardWidth = (width - S(8)) / 2;
            claudeCard = new Card(theme, scale, "Claude", strongFont, smallFont) { Location = new Point(left, y), Size = new Size(cardWidth, S(56)), Chosen = () => Choose("claude") };
            codexCard = new Card(theme, scale, "Codex", strongFont, smallFont) { Location = new Point(left + cardWidth + S(8), y), Size = new Size(cardWidth, S(56)), Chosen = () => Choose("codex") };
            Controls.Add(claudeCard);
            Controls.Add(codexCard);
            y = claudeCard.Bottom + S(12);
            int buttonWidth = S(144);
            providerNote = AddText("", smallFont, theme.Muted, left, y, width - buttonWidth - S(16), 2);
            signIn = AddButton("Sign in", left + width - buttonWidth, y, buttonWidth, () =>
            {
                Ai.OpenSignIn(host.Prefs.Provider);
            });
            y = Math.Max(providerNote.Bottom, signIn.Bottom) + S(24);

            // What it does on its own.
            y = AddText("Automatic features", headingFont, theme.Text, left, y, width).Bottom + S(12);
            string current = host.Prefs.Provider == "codex" ? "Codex" : "Claude";
            y = AddRow("Check grammar as I type", GrammarText(current), out grammarNote, host.Prefs.AutoGrammar, on => { host.Prefs.AutoGrammar = on; host.SavePrefs(); }, left, y, width);
            y = AddRow("Suggest replies in chat apps", RepliesText(current), out repliesNote, host.Prefs.AutoReplies, on => { host.Prefs.AutoReplies = on; host.SavePrefs(); }, left, y, width);
            Caption unused;
            y = AddRow("Learn the words I type", "Saved on this PC only, as single words and word pairs.", out unused, host.Prefs.LearnWords, on => { host.Prefs.LearnWords = on; host.SavePrefs(); }, left, y, width);
            y = AddRow("Start with Windows", "Otherwise, open it from the Start menu when you need it.", out unused, host.StartsWithWindows, host.SetStartWithWindows, left, y, width);
            y += S(8);

            // Shortcuts.
            y = AddText("Keyboard shortcuts", headingFont, theme.Text, left, y, width).Bottom + S(12);
            y = AddShortcut("Insert suggestion 1, 2 or 3", "Alt+1  Alt+2  Alt+3", left, y, width) + S(8);
            y = AddShortcut("Apply a grammar fix", "Ctrl+Alt+F", left, y, width) + S(24);

            // Footer.
            int forgetWidth = S(176), quitWidth = S(112);
            AddButton("Forget learned words", left, y, forgetWidth, ForgetWords);
            AddButton("Quit", left + width - quitWidth, y, quitWidth, host.Quit);
            y += S(32) + S(12);
            y = AddText("Version " + Application.ProductVersion + ". Closing this window keeps the app running in the tray.", smallFont, theme.Muted, left, y, width).Bottom + S(Margin24);

            UpdateProvider();
            var area = Screen.FromHandle(Handle).WorkingArea;
            int clientWidth = left * 2 + width;
            int maxClient = area.Height - (Height - ClientSize.Height) - S(16);
            if (y > maxClient) clientWidth += SystemInformation.VerticalScrollBarWidth;
            AutoScrollMinSize = new Size(0, y);
            ClientSize = new Size(clientWidth, Math.Min(y, maxClient));
            KeepOnScreen(area);
            ResumeLayout();
            Invalidate(true);
        }

        // Centered the first time; after that only moved if a rebuild left it hanging off the screen.
        bool placed;
        void KeepOnScreen(Rectangle area)
        {
            int x = placed ? Left : area.Left + (area.Width - Width) / 2;
            int y = placed ? Top : area.Top + (area.Height - Height) / 2;
            Location = new Point(
                Math.Max(area.Left, Math.Min(x, area.Right - Width)),
                Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
            placed = true;
        }

        string GrammarText(string name) { return "After you pause, the text in the box is sent to " + name + "."; }
        string RepliesText(string name) { return "While your box is empty, recent messages on screen are sent to " + name + " when someone messages you."; }

        void Choose(string provider)
        {
            host.SetProvider(provider);
            UpdateProvider();
        }

        void UpdateProvider()
        {
            bool claudeFound = Ai.FindCli("claude") != null, codexFound = Ai.FindCli("codex") != null;
            bool usesCodex = host.Prefs.Provider == "codex";
            string name = usesCodex ? "Codex" : "Claude";
            bool found = usesCodex ? codexFound : claudeFound;
            claudeCard.Selected = !usesCodex;
            codexCard.Selected = usesCodex;
            claudeCard.Detail = claudeFound ? "CLI installed" : "CLI not found";
            codexCard.Detail = codexFound ? "CLI installed" : "CLI not found";
            providerNote.Text = found
                ? "Uses your " + name + " subscription through its own command-line tool. No API key."
                : "Install the " + name + " command-line tool to use it. Word suggestions work without it.";
            signIn.Text = "Sign in to " + name;
            signIn.Enabled = found;
            grammarNote.Text = GrammarText(name);
            repliesNote.Text = RepliesText(name);
        }

        void ForgetWords()
        {
            var answer = MessageBox.Show(this, "Delete every word this app has learned from your typing? This can't be undone.", "Forget learned words", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) host.ForgetLearnedWords();
        }

        Caption AddText(string text, Font font, Color color, int x, int y, int width, int minLines = 1)
        {
            int height = Math.Max(Caption.Measure(text, font, width), Caption.Measure("A", font, width) * minLines);
            var caption = new Caption { Text = text, Font = font, ForeColor = color, BackColor = theme.Background, Location = new Point(x, y), Size = new Size(width, height) };
            Controls.Add(caption);
            return caption;
        }

        Button AddButton(string text, int x, int y, int width, Action click)
        {
            var button = new Button
            {
                Text = text,
                Font = bodyFont,
                Location = new Point(x, y),
                Size = new Size(width, S(32)),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = theme.Background,
                ForeColor = theme.Text,
                UseMnemonic = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = theme.Border;
            button.FlatAppearance.MouseOverBackColor = theme.ChipBack;
            button.FlatAppearance.MouseDownBackColor = theme.ChipHover;
            button.Click += (s, e) => click();
            Controls.Add(button);
            return button;
        }

        int AddRow(string name, string note, out Caption noteLabel, bool value, Action<bool> changed, int x, int y, int width)
        {
            var toggle = new Toggle(theme, scale, value, name);
            toggle.Location = new Point(x + width - toggle.Width + S(ToggleInset), y - S(ToggleInset) + S(1));
            toggle.Changed = changed;
            Controls.Add(toggle);

            int textWidth = width - toggle.Width - S(16);
            var label = AddText(name, bodyFont, theme.Text, x, y, textWidth);
            noteLabel = AddText(note, smallFont, theme.Muted, x, label.Bottom + S(2), textWidth);
            // Clicking the words works too, as it does with a normal check box.
            label.Click += (s, e) => toggle.Press();
            noteLabel.Click += (s, e) => toggle.Press();
            return noteLabel.Bottom + S(8);
        }

        int AddShortcut(string action, string keys, int x, int y, int width)
        {
            int keysWidth = TextRenderer.MeasureText(keys, strongFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 1;
            AddText(action, bodyFont, theme.Text, x, y, width - keysWidth - S(8));
            var label = AddText(keys, strongFont, theme.Muted, x + width - keysWidth, y, keysWidth);
            label.AlignRight = true;
            return label.Bottom;
        }
    }
}
