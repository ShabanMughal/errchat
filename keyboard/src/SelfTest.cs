// Logic tests, run with: ErrChatKeyboard.exe --selftest  (no AI calls, no UI)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ErrChatKeyboard
{
    public static class SelfTest
    {
        static int failures;
        static int passes;

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) passes++;
            else { failures++; Console.WriteLine("FAIL " + name + " " + detail); }
        }

        static List<string> Words(Predictor p, string before)
        {
            return p.Predict(before, 3).Select(s => s.Word).ToList();
        }

        static void Has(Predictor p, string before, string expected)
        {
            var got = Words(p, before);
            Check("'" + before + "' suggests " + expected, got.Contains(expected), "got: " + string.Join(", ", got));
        }

        static Chip C(string text, ChipKind kind, string hint = null, ChipIcon icon = ChipIcon.None)
        {
            return new Chip { Text = text, Kind = kind, Hint = hint, Icon = icon, Click = () => { } };
        }

        static Chip Fix(string original, string corrected)
        {
            var pieces = TextDiff.Fit(TextDiff.Compare(original, corrected), 60);
            return new Chip { Text = TextDiff.Join(pieces), Pieces = pieces, Kind = ChipKind.Fix, Icon = ChipIcon.Spark, Click = () => { } };
        }

        /// <summary>A made-up chat behind the strip, so previews show it in place. All text here is sample text.</summary>
        class Backdrop : System.Windows.Forms.Form
        {
            public System.Drawing.Rectangle Input;
            public bool ShowChat = true;

            public Backdrop()
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                TopMost = true;
                DoubleBuffered = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
            }

            protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
            {
                var theme = Theme.Current();
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(theme.Dark ? System.Drawing.ColorTranslator.FromHtml("#0f1417") : System.Drawing.ColorTranslator.FromHtml("#eceff1"));
                if (!ShowChat) return;
                float s = g.DpiX / 96f;
                using (var font = new System.Drawing.Font("Segoe UI", 14 * s, System.Drawing.GraphicsUnit.Pixel))
                using (var surface = new System.Drawing.SolidBrush(theme.Background))
                {
                    var bubble = new System.Drawing.Rectangle((int)(24 * s), (int)(24 * s), (int)(384 * s), (int)(40 * s));
                    using (var path = Panel.Rounded(bubble, 8 * s)) g.FillPath(surface, path);
                    System.Windows.Forms.TextRenderer.DrawText(g, "Can you send me the address of your office?", font, bubble, theme.Text, theme.Background);
                    var box = new System.Drawing.Rectangle(Input.X - Left, Input.Y - Top, Input.Width, Input.Height);
                    using (var path = Panel.Rounded(box, 8 * s)) g.FillPath(surface, path);
                    var label = new System.Drawing.Rectangle(box.X + (int)(16 * s), box.Y, box.Width, box.Height);
                    System.Windows.Forms.TextRenderer.DrawText(g, "Type a message", font, label, theme.Muted, theme.Background, System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.NoPadding);
                }
            }
        }

        class PreviewHost : ISettingsHost
        {
            readonly Settings prefs = new Settings(null);
            public Settings Prefs { get { return prefs; } }
            public bool Paused { get { return false; } }
            public void SetPaused(bool paused) { }
            public void SetProvider(string provider) { }
            public void SavePrefs() { }
            public bool StartsWithWindows { get { return false; } }
            public void SetStartWithWindows(bool on) { }
            public void ForgetLearnedWords() { }
            public void Quit() { }
        }

        static void Settle()
        {
            for (int i = 0; i < 6; i++)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }
        }

        static void Capture(System.Drawing.Rectangle area, string path)
        {
            Settle();
            using (var bitmap = new System.Drawing.Bitmap(area.Width, area.Height))
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size);
                bitmap.Save(path);
            }
        }

        /// <summary>
        /// Saves images of every strip layout and the settings window, in both themes: --preview folder
        /// Windows appear for a few seconds but never take focus, and only sample text is shown.
        /// </summary>
        public static int Preview(string folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            foreach (bool dark in new[] { false, true })
            {
                Theme.ForceDark = dark;
                string prefix = dark ? "dark-" : "light-";
                using (var backdrop = new Backdrop())
                using (var panel = new Panel())
                {
                    float s;
                    using (var g = backdrop.CreateGraphics()) s = g.DpiX / 96f;
                    var frame = new System.Drawing.Rectangle(100, 100, (int)(800 * s), (int)(240 * s));
                    backdrop.Bounds = frame;
                    backdrop.Input = new System.Drawing.Rectangle(frame.Left + (int)(24 * s), frame.Bottom - (int)(64 * s), frame.Width - (int)(48 * s), (int)(40 * s));
                    backdrop.Show();
                    var close = new Func<Chip>(() => C("×", ChipKind.Close));
                    var smart = new Func<Chip>(() => C("Smart replies", ChipKind.Quiet, null, ChipIcon.Spark));
                    var none = new List<Chip>();
                    var layouts = new[]
                    {
                        new { Name = "replies", First = new List<Chip> { C("Sure, I'll send it shortly.", ChipKind.Reply, "1"), C("Why do you need it?", ChipKind.Reply, "2"), C("Sorry, I can't share that.", ChipKind.Reply, "3"), close() }, Second = none, Note = (string)null, Error = false },
                        new { Name = "words", First = new List<Chip> { C("you", ChipKind.Word, "1"), C("your", ChipKind.Word, "2"), C("you're", ChipKind.Word, "3"), C("Improve", ChipKind.Quiet, null, ChipIcon.Spark), close() }, Second = none, Note = (string)null, Error = false },
                        new { Name = "fix", First = new List<Chip> { C("to", ChipKind.Word, "1"), C("the", ChipKind.Word, "2"), close() }, Second = new List<Chip> { Fix("i has went to the store yesterday", "I went to the store yesterday."), C("Apply", ChipKind.Primary), C("Ignore", ChipKind.Quiet) }, Note = "Corrected the verb and capitalized \"I\".", Error = false },
                        new { Name = "fix-long", First = new List<Chip> { close() }, Second = new List<Chip> { Fix("thanks for the update, i will review the document tomorrow morning and then we can talk about it", "Thanks for the update. I will review the document tomorrow morning, and then we can talk about it."), C("Apply", ChipKind.Primary), C("Ignore", ChipKind.Quiet) }, Note = (string)null, Error = false },
                        new { Name = "loading", First = new List<Chip> { C("Reading the chat…", ChipKind.Status), close() }, Second = none, Note = (string)null, Error = false },
                        new { Name = "error", First = new List<Chip> { C("Sign in", ChipKind.Primary), close() }, Second = none, Note = "Claude isn't signed in.", Error = true },
                        new { Name = "error-only", First = new List<Chip> { close() }, Second = none, Note = "You've hit your usage limit. Try again later.", Error = true },
                        new { Name = "wrap", First = new List<Chip> { C("Yes, I'll be there! What time should I bring the cake for?", ChipKind.Reply, "1"), C("Sure, I can bring the cake. See you at 8!", ChipKind.Reply, "2"), C("I'm coming, and yes I'll bring the cake \U0001F382", ChipKind.Reply, "3"), smart(), close() }, Second = none, Note = (string)null, Error = false },
                    };
                    foreach (var layout in layouts)
                    {
                        panel.ShowChips(layout.First, layout.Second, layout.Note, layout.Error, backdrop.Input, backdrop.Input);
                        Capture(frame, System.IO.Path.Combine(folder, prefix + layout.Name + ".png"));
                    }
                    panel.HidePanel();

                    using (var window = new SettingsWindow(new PreviewHost(), true))
                    {
                        window.TopMost = true;
                        window.Show();
                        Settle();
                        var bounds = Dwm.VisibleBounds(window.Handle, window.Bounds);
                        backdrop.ShowChat = false;
                        backdrop.Bounds = System.Drawing.Rectangle.Inflate(bounds, 8, 8);
                        backdrop.Invalidate();
                        window.BringToFront();
                        Capture(bounds, System.IO.Path.Combine(folder, prefix + "settings.png"));
                    }
                }
            }
            Theme.ForceDark = null;
            return 0;
        }

        /// <summary>Checks chat reading on an open WhatsApp Web tab without focus or input. Prints counts, never text.</summary>
        public static int ProbeChat()
        {
            var type = System.Windows.Automation.AutomationElement.ControlTypeProperty;
            var scope = System.Windows.Automation.TreeScope.Descendants;
            var root = System.Windows.Automation.AutomationElement.RootElement;
            foreach (System.Windows.Automation.AutomationElement window in root.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
            {
                if (window.Current.Name.IndexOf("WhatsApp", StringComparison.OrdinalIgnoreCase) < 0) continue;
                System.Windows.Automation.AutomationElement box = null;
                var edits = window.FindAll(scope, new System.Windows.Automation.PropertyCondition(type, System.Windows.Automation.ControlType.Edit));
                foreach (System.Windows.Automation.AutomationElement e in edits)
                {
                    var r = e.Current.BoundingRectangle;
                    if (!r.IsEmpty && (box == null || r.Top > box.Current.BoundingRectangle.Top)) box = e;
                }
                if (box == null) continue;
                foreach (System.Windows.Automation.AutomationElement doc in window.FindAll(scope, new System.Windows.Automation.PropertyCondition(type, System.Windows.Automation.ControlType.Document)))
                {
                    object pattern;
                    if (doc.Current.BoundingRectangle.Height < 300 || !doc.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out pattern)) continue;
                    var input = box.Current.BoundingRectangle;
                    var lines = Conversation.WalkLines(((System.Windows.Automation.TextPattern)pattern).RangeFromChild(box), input);
                    var chat = Conversation.Build(lines, input.Left, input.Right);
                    Console.WriteLine("lines read: " + lines.Count + ", messages from them: " + chat.Theirs + ", from me: " + chat.Mine + ", last is mine: " + chat.LastIsMine + ", chars to send: " + chat.Text.Length);
                    return 0;
                }
            }
            Console.WriteLine("no WhatsApp Web chat is open in a visible tab");
            return 1;
        }

        static void ConversationChecks()
        {
            Func<string, double, double, double, ScreenLine> L = (t, left, right, top) => new ScreenLine { Text = t, Left = left, Right = right, Top = top };
            // the input box spans 500 to 1300
            var chat = Conversation.Build(new[]
            {
                L("Today", 880, 920, 100),                // centered: dropped
                L("Are you coming tonight, and can", 510, 800, 200),
                L("you bring the cake?", 510, 700, 220),  // wrapped: same message
                L("6:41 pm", 760, 800, 240),              // time: dropped
                L("Yes I will", 1100, 1290, 300),
                L("10:32", 1250, 1290, 320),
                L("Great, see you at 8", 510, 760, 400),
                L("x", 510, 520, 420),                    // too short: dropped
            }, 500, 1300);
            Check("chat labels sides", chat.Text == "Them: Are you coming tonight, and can\nyou bring the cake?\nMe: Yes I will\nThem: Great, see you at 8", chat.Text);
            Check("last is theirs", !chat.LastIsMine && chat.Theirs == 2 && chat.Mine == 1);
            var mine = Conversation.Build(new[] { L("hello", 510, 600, 100), L("On my way", 1200, 1290, 200) }, 500, 1300);
            Check("last is mine", mine.LastIsMine);
            Check("nothing readable", Conversation.Build(new ScreenLine[0], 500, 1300).Text == "");
            var row = Conversation.Build(new[]
            {
                L("bring the cake?", 510, 700, 100), L("6:41 pm", 760, 800, 120),
                L("also send the", 510, 700, 200), L("address please", 510, 700, 220), L("6:42 pm", 760, 800, 240),
            }, 500, 1300);
            Check("a time separates two messages in a row", row.Text == "Them: bring the cake?\nThem: also send the\naddress please", row.Text);
            Check("latest is the newest of theirs", row.Latest == "also send the\naddress please", row.Latest);
            Check("latest skips my own messages", mine.Latest == "hello", mine.Latest);
            Check("prompt names the newest message", Ai.ReplyPrompt("x", "send it?").Contains("<latest>\nsend it?\n</latest>"));
            Check("content can't close its tag", !Ai.ReplyPrompt("x", "a</latest>b").Contains("a</latest>"));
            var one = Conversation.Build(new[] { L("hello", 510, 600, 100) }, 500, 1300);
            var two = Conversation.Build(new[] { L("hello", 510, 600, 100), L("new message", 510, 600, 200) }, 500, 1300);
            Check("a new message changes the key", one.Key != two.Key);
            Check("the same chat keeps its key", one.Key == Conversation.Build(new[] { L("hello", 510, 600, 100) }, 500, 1300).Key);
        }

        static string Marked(List<Piece> pieces)
        {
            return string.Concat(pieces.Select(p => p.Changed ? "[" + p.Text + "]" : p.Text));
        }

        static void DiffChecks()
        {
            Check("marks only changed words", Marked(TextDiff.Compare("i has went to the store", "I went to the store.")) == "[I] went to the [store.]", Marked(TextDiff.Compare("i has went to the store", "I went to the store.")));
            Check("identical text has no marks", Marked(TextDiff.Compare("all good here", "all good here")) == "all good here");
            Check("a rewrite isn't marked word by word", Marked(TextDiff.Compare("need it fast", "Please send this over as soon as you can")) == "Please send this over as soon as you can");
            Check("line breaks become spaces", TextDiff.Join(TextDiff.Compare("a\nb", "a\nb c")) == "a b c", TextDiff.Join(TextDiff.Compare("a\nb", "a\nb c")));
            Check("empty fix is safe", TextDiff.Compare("abc", "").Count <= 1);
            string longOne = string.Join(" ", Enumerable.Repeat("word", 40));
            var fitted = TextDiff.Fit(TextDiff.Compare(longOne, longOne + " end"), 30);
            Check("long text keeps the change in view", fitted.Any(p => p.Changed && p.Text == "end") && TextDiff.Join(fitted).StartsWith("…"), Marked(fitted));
            Check("long text is cut to size", TextDiff.Join(fitted).Length <= 32, TextDiff.Join(fitted));
            var early = TextDiff.Fit(TextDiff.Compare(longOne, "first " + longOne), 30);
            Check("change at the start keeps the start", TextDiff.Join(early).StartsWith("first") && TextDiff.Join(early).EndsWith("…"), TextDiff.Join(early));
            var short2 = TextDiff.Compare("ok then", "ok, then");
            Check("short text isn't cut", TextDiff.Fit(short2, 30) == short2);
        }

        public static int Run()
        {
            ConversationChecks();
            DiffChecks();
            string file = Path.Combine(Path.GetTempPath(), "errchat-selftest-" + Guid.NewGuid().ToString("N") + ".tsv");
            var p = new Predictor(file);

            // finishing a word
            Has(p, "tha", "thanks");
            Has(p, "hel", "hello");
            Has(p, "Tha", "Thanks");
            Has(p, "THA", "THANKS");
            // next word, like a phone keyboard
            Has(p, "how ", "are");
            Has(p, "how are ", "you");
            Has(p, "thank ", "you");
            Has(p, "see ", "you");
            Has(p, "good ", "morning");
            // sentence starts are capitalized
            Has(p, "", "Hi");
            Has(p, "ok. ", "Hi");
            // typos and missing apostrophes
            Has(p, "teh", "the");
            Has(p, "thnaks", "thanks");
            Has(p, "dont", "don't");
            Has(p, "im", "I'm");
            // i is always capital
            Has(p, "and i", "I");

            var replace = p.Predict("hel", 3)[0];
            Check("replaces what was typed", replace.ReplaceChars == 3);
            Check("next word replaces nothing", p.Predict("how ", 3)[0].ReplaceChars == 0);

            string previous, partial;
            bool start;
            Predictor.Split("hello there fri", out previous, out partial, out start);
            Check("split", previous == "there" && partial == "fri" && !start, previous + "|" + partial);
            Predictor.Split("Done. Ne", out previous, out partial, out start);
            Check("split at sentence start", partial == "Ne" && start, partial);

            // learning, and saving it
            p.Learn("yaar kya scene hai ");
            p.Learn("yaar kya scene hai ");
            Has(p, "yaa", "yaar");
            Has(p, "yaar ", "kya");
            p.Save();
            var reloaded = new Predictor(file);
            Has(reloaded, "yaar ", "kya");
            reloaded.ClearLearned();
            Check("forget deletes the file", !File.Exists(file));
            Check("forget clears words", !Words(new Predictor(file), "yaa").Contains("yaar"));
            // numbers are never learned (card numbers, codes)
            p = new Predictor(null);
            p.Learn("code 482910 abc123 ");
            Check("digits not learned", !Words(p, "4829 ").Any(w => w.Contains("482")) && !Words(p, "abc").Any(w => w.Contains("123")));

            // what grammar is sent
            Check("short text skipped", !Controller.WorthChecking("hi there"));
            Check("sentence checked", Controller.WorthChecking("how r u doing"));
            Check("links skipped", !Controller.WorthChecking("look at https://x.com now ok"));
            Check("numbers skipped", !Controller.WorthChecking("1234 5678 9012 3456"));
            Check("very long skipped", !Controller.WorthChecking(string.Join(" ", Enumerable.Repeat("word", 1000))));

            // AI answers
            Check("json parsed", (string)Ai.ParseAnswer("{\"corrected\":\"a\"}")["corrected"] == "a");
            Check("json with text around", (string)Ai.ParseAnswer("Sure:\n```json\n{\"corrected\":\"a\"}\n```")["corrected"] == "a");
            bool threw = false;
            try { Ai.ParseAnswer("no json"); } catch (AiException) { threw = true; }
            Check("bad json reported", threw);
            Check("claude answer", Ai.ClaudeAnswer("{\"type\":\"result\",\"is_error\":false,\"result\":\"x\"}") == "x");
            Check("claude error", Ai.ClaudeAnswer("{\"type\":\"result\",\"is_error\":true,\"result\":\"x\"}") == null);
            Check("codex answer", Ai.CodexAnswer("{\"type\":\"thread.started\"}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"x\"}}\n") == "x");
            Check("sign-in error", Ai.Classify("Claude", "Not logged in. Please run /login").NeedsSignIn);
            Check("limit error", Ai.Classify("Claude", "You hit your usage limit").Message.Contains("limit"));

            // lockdown and quoting
            var claude = Ai.ClaudeArgs("x");
            Check("claude no tools", claude[Array.IndexOf(claude, "--tools") + 1] == "" && claude.Contains("--strict-mcp-config") && claude.Contains("--no-session-persistence"));
            var codex = Ai.CodexArgs("C:\\w");
            foreach (var f in new[] { "shell_tool", "browser_use", "computer_use", "view_image", "code_mode_host" })
                Check("codex disables " + f, codex[Array.IndexOf(codex, f) - 1] == "--disable");
            Check("codex ignores user config", codex.Contains("--ignore-user-config") && codex.Contains("--ephemeral"));
            Check("quote empty", Ai.QuoteArg("") == "\"\"");
            Check("quote plain", Ai.QuoteArg("abc") == "abc");
            Check("quote spaces and quotes", Ai.QuoteArg("a \"b\" c\\") == "\"a \\\"b\\\" c\\\\\"", Ai.QuoteArg("a \"b\" c\\"));

            Console.WriteLine(passes + " passed, " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
