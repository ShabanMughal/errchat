// Ties it together: watches the focused box, decides what the panel shows, runs AI calls.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ErrChatKeyboard
{
    public class Settings
    {
        public string Provider = "claude";
        public bool AutoGrammar = true;
        public bool LearnWords = true;
        public bool AutoReplies = true;
        /// <summary>Where the badge and strip were dragged to; null follows the box.</summary>
        public Point? BadgeAt, StripAt;
        readonly string file;

        public Settings(string path)
        {
            file = path;
            try
            {
                if (!File.Exists(file)) return;
                foreach (var line in File.ReadAllLines(file))
                {
                    var p = line.Split(new[] { '=' }, 2);
                    if (p.Length != 2) continue;
                    if (p[0] == "provider" && (p[1] == "claude" || p[1] == "codex")) Provider = p[1];
                    if (p[0] == "autoGrammar") AutoGrammar = p[1] == "true";
                    if (p[0] == "learnWords") LearnWords = p[1] == "true";
                    if (p[0] == "autoReplies") AutoReplies = p[1] == "true";
                    if (p[0] == "badgeAt") BadgeAt = ParsePoint(p[1]);
                    if (p[0] == "stripAt") StripAt = ParsePoint(p[1]);
                }
            }
            catch (IOException) { }
        }

        static Point? ParsePoint(string text)
        {
            var parts = text.Split(',');
            int x, y;
            if (parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y)) return new Point(x, y);
            return null;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "provider=" + Provider,
                    "autoGrammar=" + (AutoGrammar ? "true" : "false"),
                    "learnWords=" + (LearnWords ? "true" : "false"),
                    "autoReplies=" + (AutoReplies ? "true" : "false")
                };
                if (BadgeAt.HasValue) lines.Add("badgeAt=" + BadgeAt.Value.X + "," + BadgeAt.Value.Y);
                if (StripAt.HasValue) lines.Add("stripAt=" + StripAt.Value.X + "," + StripAt.Value.Y);
                File.WriteAllLines(file, lines);
            }
            catch (IOException) { }
        }
    }

    public class Controller : ApplicationContext, ISettingsHost
    {
        const int GrammarDelayMs = 1500;
        const int MinWordsToCheck = 3;
        const int LearnSaveMs = 30000;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ErrChatKeyboard";
        const int ChatCheckMs = 2500;
        const int MinSecondsBetweenAutoReplies = 8;
        const int MaxCachedChats = 50;
        const int FixChars = 60;
        public const string ShowSettingsEvent = "ErrChatKeyboard-show-settings";
        // Chat text is only read by itself in these apps; elsewhere it takes a click on Smart replies.
        static readonly System.Text.RegularExpressions.Regex ChatApps = new System.Text.RegularExpressions.Regex(
            @"\b(WhatsApp|Telegram|Messenger|Discord|Slack|Teams|ms-teams|Signal|Instagram)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        readonly Panel panel = new Panel();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Settings settings;
        readonly Predictor predictor;
        readonly FocusWatcher watcher;
        readonly System.Windows.Forms.Timer grammarTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();

        Snapshot snap;
        string elementKey = "";
        string lastBefore = "";
        bool dismissed;
        bool paused;
        List<Suggestion> words = new List<Suggestion>();

        // grammar state
        AiCall grammarCall;
        bool checking;
        string checkedText = "";
        FixResult fix;
        string fixFor = "";
        bool improving;
        AiCall improveCall;

        // smart reply state
        AiCall replyCall;
        bool loadingReplies;
        List<string> smartReplies;
        readonly Dictionary<string, List<string>> replyCache = new Dictionary<string, List<string>>();
        readonly System.Windows.Forms.Timer chatTimer = new System.Windows.Forms.Timer();
        string requestedKey = "";
        string seenKey = "";
        bool waitingToAsk;
        DateTime lastAutoReply = DateTime.MinValue;
        bool readingChat;

        string status;
        bool statusNeedsSignIn;
        volatile bool typing;
        SettingsWindow settingsWindow;
        EventWaitHandle showSettingsSignal;

        public Controller()
        {
            // Tests point this at a throwaway folder so they don't touch learned words.
            string dir = Environment.GetEnvironmentVariable("ERRCHAT_KB_DATA");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ErrChatKeyboard");
            Directory.CreateDirectory(dir);
            string settingsFile = Path.Combine(dir, "settings.txt");
            bool firstRun = !File.Exists(settingsFile);
            settings = new Settings(settingsFile);
            predictor = new Predictor(Path.Combine(dir, "learned.tsv"));

            panel.HotkeyWord = PickByNumber;
            panel.HotkeyApplyFix = ApplyFix;
            panel.BadgePin = settings.BadgeAt;
            panel.StripPin = settings.StripAt;
            panel.Moved = () =>
            {
                settings.BadgeAt = panel.BadgePin;
                settings.StripAt = panel.StripPin;
                settings.Save();
            };
            panel.HideForBox = () => { dismissed = true; CancelAi(); panel.HidePanel(); };
            grammarTimer.Interval = GrammarDelayMs;
            grammarTimer.Tick += (s, e) => { grammarTimer.Stop(); StartGrammarCheck(); };
            saveTimer.Interval = LearnSaveMs;
            saveTimer.Tick += (s, e) => predictor.Save();
            saveTimer.Start();
            chatTimer.Interval = ChatCheckMs;
            chatTimer.Tick += (s, e) => CheckChat(false);
            chatTimer.Start();

            BuildTray();
            watcher = new FocusWatcher(s =>
            {
                try { panel.BeginInvoke((Action)(() => OnSnapshot(s))); }
                catch (InvalidOperationException) { } // shutting down
            });
            watcher.Start();
            // Opening the app while it's already running brings up settings.
            showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
            ThreadPool.RegisterWaitForSingleObject(showSettingsSignal, (state, timedOut) =>
            {
                try { panel.BeginInvoke((Action)ShowSettings); }
                catch (InvalidOperationException) { } // shutting down
            }, null, Timeout.Infinite, false);
            if (firstRun)
            {
                // Shown once so people see what gets sent, and can turn it off, before it happens.
                settings.Save();
                ShowSettings();
            }
        }

        // --- settings window ---

        public void ShowSettings()
        {
            if (settingsWindow == null || settingsWindow.IsDisposed)
            {
                settingsWindow = new SettingsWindow(this);
                settingsWindow.Show();
            }
            if (settingsWindow.WindowState == FormWindowState.Minimized) settingsWindow.WindowState = FormWindowState.Normal;
            settingsWindow.Activate();
        }

        public Settings Prefs { get { return settings; } }
        public bool Paused { get { return paused; } }
        public void SavePrefs() { settings.Save(); }
        public bool StartsWithWindows { get { return ReadStartup(); } }
        public void ForgetLearnedWords() { predictor.ClearLearned(); }
        public bool PositionIsCustom { get { return settings.BadgeAt.HasValue || settings.StripAt.HasValue; } }

        public void ResetPosition()
        {
            settings.BadgeAt = null;
            settings.StripAt = null;
            settings.Save();
            panel.ClearPins();
        }

        public void SetPaused(bool value)
        {
            paused = value;
            watcher.Paused = paused;
            if (paused) panel.HidePanel();
            RebuildMenu();
        }

        class MenuColors : ProfessionalColorTable
        {
            readonly Theme theme;
            public MenuColors(Theme t) { theme = t; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return theme.Background; } }
            public override Color ImageMarginGradientBegin { get { return theme.Background; } }
            public override Color ImageMarginGradientMiddle { get { return theme.Background; } }
            public override Color ImageMarginGradientEnd { get { return theme.Background; } }
            public override Color MenuBorder { get { return theme.Frame; } }
            public override Color MenuItemBorder { get { return theme.ChipHover; } }
            public override Color MenuItemSelected { get { return theme.ChipHover; } }
            public override Color SeparatorDark { get { return theme.Frame; } }
            public override Color SeparatorLight { get { return theme.Background; } }
        }

        class MenuRenderer : ToolStripProfessionalRenderer
        {
            readonly Theme theme;
            public MenuRenderer(Theme t) : base(new MenuColors(t)) { theme = t; RoundedEdges = false; }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? theme.Text : theme.Muted;
                base.OnRenderItemText(e);
            }
        }

        // --- tray ---

        void BuildTray()
        {
            // The tray needs the small version: shrinking the large one blurs it.
            using (var stream = typeof(Controller).Assembly.GetManifestResourceStream("icon.ico"))
                tray.Icon = new Icon(stream, SystemInformation.SmallIconSize);
            tray.Text = "ErrChat Keyboard";
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
            RebuildMenu();
        }

        void RebuildMenu()
        {
            var menu = new ContextMenuStrip();
            // Read the theme each time, so the menu follows a change in Windows.
            menu.Opening += (s, e) => { menu.Renderer = new MenuRenderer(Theme.Current()); };
            var state = new ToolStripMenuItem(paused ? "Paused" : "Using " + (settings.Provider == "codex" ? "Codex" : "Claude")) { Enabled = false };
            menu.Items.Add(state);
            menu.Items.Add(new ToolStripSeparator());
            var pause = new ToolStripMenuItem(paused ? "Resume suggestions" : "Pause suggestions");
            pause.Click += (s, e) => SetPaused(!paused);
            menu.Items.Add(pause);
            var open = new ToolStripMenuItem("Settings");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += (s, e) => ShowSettings();
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Quit");
            exit.Click += (s, e) => Quit();
            menu.Items.Add(exit);
            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        public void SetProvider(string provider)
        {
            settings.Provider = provider;
            settings.Save();
            status = null;
            statusNeedsSignIn = false;
            RebuildMenu();
        }

        static bool ReadStartup()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(RunName) != null;
        }

        public void SetStartWithWindows(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
        }

        public void Quit()
        {
            if (settingsWindow != null && !settingsWindow.IsDisposed) settingsWindow.Close();
            watcher.Stop();
            CancelAi();
            predictor.Save();
            tray.Visible = false;
            tray.Dispose();
            panel.HidePanel();
            ExitThread();
        }

        // --- focus and typing ---

        void OnSnapshot(Snapshot s)
        {
            if (typing) return;
            if (s == null)
            {
                // Clicking our own panel never reaches here: it doesn't take focus.
                if (snap != null) Reset();
                snap = null;
                panel.HidePanel();
                return;
            }

            string key = s.Window.ToInt64() + ":" + string.Join(",", s.ElementId);
            if (key != elementKey)
            {
                Reset();
                elementKey = key;
                lastBefore = s.Before;
                snap = s;
                CheckChat(false); // don't wait for the timer when a box gets focus
            }
            snap = s;
            if (dismissed) { panel.HidePanel(); return; }

            if (s.Before != lastBefore)
            {
                LearnFrom(lastBefore, s.Before);
                lastBefore = s.Before;
            }
            words = s.HasSelection ? new List<Suggestion>() : predictor.Predict(s.Before, 3);

            string text = s.CheckText.Trim();
            if (text != fixFor && fix != null) { fix = null; }
            if (text.Length > 0 && smartReplies != null && s.Before.Trim().Length > 0) smartReplies = null;
            ScheduleGrammar(text);
            Draw();
        }

        void Reset()
        {
            CancelAi();
            grammarTimer.Stop();
            panel.SetExpanded(false); // every box starts as the badge, so nothing covers its text
            elementKey = "";
            dismissed = false;
            fix = null;
            fixFor = "";
            checkedText = "";
            smartReplies = null;
            status = null;
            lastBefore = "";
        }

        void LearnFrom(string previous, string current)
        {
            if (!settings.LearnWords || !current.StartsWith(previous, StringComparison.Ordinal)) return;
            string added = current.Substring(previous.Length);
            // Learn whole words only, when a space or punctuation finishes them.
            if (added.Length == 0 || char.IsLetter(added[added.Length - 1])) return;
            int start = previous.Length;
            while (start > 0 && !char.IsWhiteSpace(current[start - 1])) start--;
            predictor.Learn(current.Substring(start));
        }

        // --- what the panel shows ---

        void Draw()
        {
            if (snap == null) return;
            var first = new List<Chip>();
            var second = new List<Chip>();
            string note = status;
            bool noteError = status != null;
            bool empty = snap.Before.Trim().Length == 0 && snap.CheckText.Trim().Length == 0;

            if (fix != null)
            {
                var pieces = TextDiff.Fit(TextDiff.Compare(fixFor, fix.Corrected), FixChars);
                second.Add(new Chip { Text = TextDiff.Join(pieces), Pieces = pieces, Kind = ChipKind.Fix, Icon = ChipIcon.Spark, Click = ApplyFix, Tip = fix.Corrected });
                second.Add(new Chip { Text = "Apply", Kind = ChipKind.Primary, Click = ApplyFix, Tip = "Ctrl+Alt+F" });
                second.Add(new Chip { Text = "Ignore", Kind = ChipKind.Quiet, Click = IgnoreFix });
                if (note == null && !string.IsNullOrEmpty(fix.Reason)) note = fix.Reason;
            }

            if (empty)
            {
                bool busy = loadingReplies || waitingToAsk;
                if (busy) first.Add(new Chip { Text = "Reading the chat…", Kind = ChipKind.Status });
                if (smartReplies != null)
                    for (int i = 0; i < smartReplies.Count; i++)
                    {
                        string reply = smartReplies[i];
                        first.Add(new Chip
                        {
                            Text = Shorten(reply, 60),
                            Hint = i < 3 ? (i + 1).ToString() : null,
                            Tip = reply.Length > 60 ? reply : null,
                            Kind = ChipKind.Reply,
                            Click = Insert(reply + " ", 0)
                        });
                    }
                if (!busy) first.Add(new Chip { Text = "Smart replies", Kind = ChipKind.Quiet, Icon = ChipIcon.Spark, Click = () => CheckChat(true) });
            }
            else
            {
                for (int i = 0; i < words.Count; i++)
                {
                    var word = words[i];
                    first.Add(new Chip { Text = word.Word, Hint = (i + 1).ToString(), Kind = ChipKind.Word, Click = Insert(word.Word + " ", word.ReplaceChars) });
                }
                if (checking) first.Add(new Chip { Text = "Checking…", Kind = ChipKind.Status });
                else if (improving) first.Add(new Chip { Text = "Rewriting…", Kind = ChipKind.Status });
                else if (fix == null && snap.CheckText.Trim().Length > 0) first.Add(new Chip { Text = "Improve", Kind = ChipKind.Quiet, Icon = ChipIcon.Spark, Click = StartImprove });
            }
            if (statusNeedsSignIn) first.Add(new Chip { Text = "Sign in", Kind = ChipKind.Primary, Click = () => { Ai.OpenSignIn(settings.Provider); status = null; statusNeedsSignIn = false; } });
            first.Add(new Chip { Text = "×", Kind = ChipKind.Close, Tip = "Close (Ctrl+Alt+Space)", Click = () => panel.SetExpanded(false) });

            var e = snap.ElementRect;
            var c = snap.CaretRect;
            var anchor = e.IsEmpty ? Rectangle.Empty : new Rectangle((int)e.Left, (int)e.Top, (int)e.Width, (int)e.Height);
            var caret = c.IsEmpty ? anchor : new Rectangle((int)c.Left, (int)c.Top, (int)c.Width, (int)c.Height);
            panel.ShowChips(first, second, note, noteError, anchor, caret);
        }

        static string Shorten(string text, int max)
        {
            string line = text.Replace("\r", " ").Replace("\n", " ");
            return line.Length > max ? line.Substring(0, max) + "…" : line;
        }

        // --- inserting ---

        Action Insert(string text, int replaceChars)
        {
            return () =>
            {
                if (snap == null) return;
                var window = snap.Window;
                smartReplies = null;
                RunTyping(() => Typer.TypeWord(replaceChars, text, window));
            };
        }

        void RunTyping(Action action)
        {
            typing = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { action(); }
                finally
                {
                    Thread.Sleep(60);
                    typing = false;
                }
            });
        }

        void PickByNumber(int index)
        {
            if (snap == null || dismissed) return;
            bool empty = snap.Before.Trim().Length == 0 && snap.CheckText.Trim().Length == 0;
            if (empty)
            {
                if (smartReplies != null && index < smartReplies.Count) Insert(smartReplies[index] + " ", 0)();
            }
            else if (index < words.Count)
            {
                Insert(words[index].Word + " ", words[index].ReplaceChars)();
            }
        }

        // --- grammar ---

        void ScheduleGrammar(string text)
        {
            if (text == scheduledFor) return;
            scheduledFor = text;
            if (grammarCall != null) { grammarCall.Cancel(); grammarCall = null; checking = false; }
            grammarTimer.Stop();
            if (settings.AutoGrammar && WorthChecking(text) && text != checkedText) grammarTimer.Start();
        }
        string scheduledFor = "";

        /// <summary>Skips short text and things that aren't sentences, like links and numbers.</summary>
        public static bool WorthChecking(string text)
        {
            if (text.Length == 0 || text.Length > FocusWatcher.MaxCheck) return false;
            if (text.Contains("://") || text.Contains("www.")) return false;
            var parts = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= MinWordsToCheck && parts.Count(p => p.Any(char.IsLetter)) >= MinWordsToCheck;
        }

        void StartGrammarCheck()
        {
            if (snap == null || dismissed) return;
            string text = snap.CheckText.Trim();
            if (!WorthChecking(text) || text == checkedText) return;
            checkedText = text;
            checking = true;
            var call = new AiCall();
            grammarCall = call;
            RunFix(text, false, call, () => { checking = false; if (grammarCall == call) grammarCall = null; });
            Draw();
        }

        void StartImprove()
        {
            if (snap == null) return;
            string text = snap.CheckText.Trim();
            if (text.Length == 0 || text.Length > FocusWatcher.MaxCheck) return;
            if (grammarCall != null) { grammarCall.Cancel(); grammarCall = null; checking = false; }
            grammarTimer.Stop();
            checkedText = text;
            improving = true;
            var call = new AiCall();
            improveCall = call;
            RunFix(text, true, call, () => { improving = false; if (improveCall == call) improveCall = null; });
            Draw();
        }

        void RunFix(string text, bool improve, AiCall call, Action finished)
        {
            string provider = settings.Provider;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                FixResult result = null;
                AiException error = null;
                try { result = Ai.Fix(provider, text, improve, call); }
                catch (OperationCanceledException) { }
                catch (AiException e) { error = e; }
                catch (Exception e) { error = new AiException(e.Message); }
                panel.BeginInvoke((Action)(() =>
                {
                    finished();
                    if (call.Cancelled) return;
                    SetError(error);
                    if (result != null && snap != null && snap.CheckText.Trim() == text)
                    {
                        string corrected = result.Corrected.Trim();
                        if (corrected != text)
                        {
                            fix = result;
                            fix.Corrected = corrected;
                            fixFor = text;
                        }
                    }
                    Draw();
                }));
            });
        }

        void SetError(AiException error)
        {
            status = error == null ? null : error.Message;
            statusNeedsSignIn = error != null && error.NeedsSignIn;
        }

        void ApplyFix()
        {
            if (fix == null || snap == null) return;
            string corrected = fix.Corrected;
            var range = snap.CheckRange;
            var window = snap.Window;
            checkedText = corrected; // the result shouldn't be checked again
            scheduledFor = corrected;
            fix = null;
            fixFor = "";
            status = null;
            RunTyping(() => Typer.Replace(range, corrected, window));
            Draw();
        }

        void IgnoreFix()
        {
            fix = null;
            fixFor = "";
            status = null;
            Draw();
        }

        // --- smart replies ---

        bool BoxIsEmpty()
        {
            return snap != null && snap.Before.Trim().Length == 0 && snap.CheckText.Trim().Length == 0;
        }

        /// <summary>Reads the chat while the box is empty. force is a click on Smart replies.</summary>
        void CheckChat(bool force)
        {
            if (paused || dismissed || readingChat || !BoxIsEmpty()) return;
            bool auto = settings.AutoReplies && ChatApps.IsMatch(snap.App);
            if (!force && !auto) return;
            var current = snap;
            readingChat = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ChatText chat = null;
                try { chat = Conversation.Read(current); }
                catch (System.Windows.Automation.ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (System.Runtime.InteropServices.COMException) { }
                catch (ArgumentException) { }
                panel.BeginInvoke((Action)(() =>
                {
                    readingChat = false;
                    OnChatRead(chat, current, force);
                }));
            });
        }

        void OnChatRead(ChatText chat, Snapshot from, bool force)
        {
            // The user may have moved on or started typing while this was being read.
            if (snap == null || snap.Window != from.Window || !BoxIsEmpty()) return;
            if (chat == null || chat.Text.Length == 0)
            {
                if (force) { status = "No chat text is readable in this app."; Draw(); }
                return;
            }
            bool changed = chat.Key != seenKey;
            if (changed) Panel.TraceChat("key changed: [" + chat.Key.Replace("\n", "\\n") + "]");
            seenKey = chat.Key;
            List<string> cached;
            if (replyCache.TryGetValue(chat.Key, out cached))
            {
                waitingToAsk = false;
                if (smartReplies != cached) { smartReplies = cached; Draw(); }
                return;
            }
            if (!force)
            {
                // Saves quota: nothing to answer if the last message is yours, and no call
                // unless the chat changed, and not more than once every few seconds.
                if (chat.LastIsMine)
                {
                    waitingToAsk = false;
                    if (smartReplies != null) { smartReplies = null; Draw(); }
                    return;
                }
                if (chat.Key == requestedKey) return;
                if ((DateTime.Now - lastAutoReply).TotalSeconds < MinSecondsBetweenAutoReplies)
                {
                    // Replies to the previous message would now be wrong, so don't leave them up.
                    if (smartReplies != null || !waitingToAsk)
                    {
                        smartReplies = null;
                        waitingToAsk = true;
                        Draw();
                    }
                    return;
                }
            }
            lastAutoReply = DateTime.Now;
            waitingToAsk = false;
            RequestReplies(chat);
        }

        void RequestReplies(ChatText chat)
        {
            if (replyCall != null) replyCall.Cancel();
            string provider = settings.Provider;
            string key = chat.Key;
            var call = new AiCall();
            replyCall = call;
            requestedKey = key;
            smartReplies = null; // replies to an older message would now be wrong
            loadingReplies = true;
            status = null;
            statusNeedsSignIn = false;
            Draw();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<string> replies = null;
                AiException error = null;
                try { replies = Ai.Replies(provider, chat.Text, chat.Latest, call); }
                catch (OperationCanceledException) { }
                catch (AiException e) { error = e; }
                catch (Exception e) { error = new AiException(e.Message); }
                panel.BeginInvoke((Action)(() =>
                {
                    if (replyCall != call) return; // a newer message replaced this request
                    replyCall = null;
                    loadingReplies = false;
                    if (call.Cancelled) return;
                    SetError(error);
                    if (replies != null && replies.Count > 0)
                    {
                        if (replyCache.Count >= MaxCachedChats) replyCache.Clear();
                        replyCache[key] = replies;
                        // If another message came in meanwhile, these answer the wrong one.
                        if (requestedKey == key && seenKey == key) smartReplies = replies;
                    }
                    Draw();
                }));
            });
        }

        void CancelAi()
        {
            if (grammarCall != null) { grammarCall.Cancel(); grammarCall = null; }
            if (improveCall != null) { improveCall.Cancel(); improveCall = null; }
            if (replyCall != null) { replyCall.Cancel(); replyCall = null; }
            checking = improving = loadingReplies = false;
            scheduledFor = "";
            requestedKey = "";
            waitingToAsk = false;
        }
    }

    public static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
            if (args.Length > 1 && args[0] == "--preview") return SelfTest.Preview(args[1]);
            if (args.Length > 0 && args[0] == "--probe-chat") return SelfTest.ProbeChat();
            if (args.Length > 0 && args[0] == "--check-panel") return SelfTest.CheckPanel();
            bool created;
            using (var mutex = new Mutex(true, "ErrChatKeyboard-single-instance", out created))
            {
                if (!created)
                {
                    // Already running: the double-click should still do something visible.
                    try
                    {
                        AllowSetForegroundWindow(-1);
                        using (var signal = EventWaitHandle.OpenExisting(Controller.ShowSettingsEvent)) signal.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Controller());
            }
            return 0;
        }
    }
}
