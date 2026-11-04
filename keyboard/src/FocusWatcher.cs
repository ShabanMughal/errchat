// Reads the focused text box in whichever app is in front, through Windows UI
// Automation. It reads the field on demand; it does not hook the keyboard.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace ErrChatKeyboard
{
    public class Snapshot
    {
        public IntPtr Window;
        public int[] ElementId;
        public AutomationElement Element;
        public string Before = "";      // text before the caret, up to MaxBefore characters
        public string CheckText = "";   // the text grammar checks: the field, or the paragraph in long documents
        public TextPatternRange CheckRange; // null when the field only supports ValuePattern
        public TextPatternRange Caret;
        public string App = "";         // window title and process name
        public bool HasSelection;
        public Rect ElementRect;
        public Rect CaretRect;
    }

    public class FocusWatcher
    {
        const int PollMs = 150;
        const int MaxBefore = 600;
        public const int MaxCheck = 2000;
        const int MaxScreenChars = 3000;
        const int MaxScreenLines = 40;

        // Terminals and code editors: suggestions there would only get in the way.
        static readonly HashSet<string> SkippedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "Code", "devenv", "idea64", "pycharm64",
            "ErrChatKeyboard", "ErrChat", "KeePass", "1Password", "Bitwarden", "LockApp", "LogonUI", "CredentialUIBroker"
        };

        // Browser address bars aren't messages.
        static readonly HashSet<string> SkippedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OmniboxViewViews", "AddressBar"
        };

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        static string WindowTitle(IntPtr window)
        {
            var sb = new StringBuilder(256);
            GetWindowText(window, sb, sb.Capacity);
            return sb.ToString();
        }

        readonly Action<Snapshot> onSnapshot;
        volatile bool running = true;
        public volatile bool Paused;
        readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();

        public FocusWatcher(Action<Snapshot> callback)
        {
            onSnapshot = callback;
        }

        public void Start()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "focus" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop() { running = false; }

        void Loop()
        {
            while (running)
            {
                Snapshot snapshot = null;
                try { if (!Paused) snapshot = Read(); }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
                catch (ArgumentException) { }
                onSnapshot(snapshot);
                Thread.Sleep(PollMs);
            }
        }

        string ProcessName(uint pid)
        {
            string name;
            if (processNames.TryGetValue(pid, out name)) return name;
            try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch (ArgumentException) { name = ""; }
            if (processNames.Count > 200) processNames.Clear();
            processNames[pid] = name;
            return name;
        }

        Snapshot Read()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (SkippedProcesses.Contains(ProcessName(pid))) return null;

            var element = AutomationElement.FocusedElement;
            if (element == null) return null;
            var current = element.Current;
            if (current.IsPassword || !current.IsEnabled) return null;
            if (SkippedClasses.Contains(current.ClassName ?? "")) return null;
            var type = current.ControlType;
            if (type != ControlType.Edit && type != ControlType.Document) return null;

            object valueObj = null, textObj = null;
            bool hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out valueObj);
            bool hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out textObj);
            if (hasValue && ((ValuePattern)valueObj).Current.IsReadOnly) return null;
            if (!hasValue && !hasText) return null;
            // A Document with no value pattern and no text pattern selection is a read-only page.
            var snapshot = new Snapshot
            {
                Window = window,
                Element = element,
                ElementId = element.GetRuntimeId(),
                ElementRect = current.BoundingRectangle,
                App = WindowTitle(window) + " " + ProcessName(pid)
            };

            // The value is the field's real text. Chrome's text range reports an empty
            // box's label ("Type a message") as if it had been typed.
            string value = hasValue ? ((ValuePattern)valueObj).Current.Value ?? "" : null;

            if (hasText && ReadTextPattern((TextPattern)textObj, snapshot, value)) return snapshot;
            if (type == ControlType.Document && !hasValue) return null;
            if (hasValue)
            {
                snapshot.Before = Tail(value);
                snapshot.CheckText = value.Length <= MaxCheck ? value : "";
                snapshot.CaretRect = snapshot.ElementRect;
                return snapshot;
            }
            return null;
        }

        static string Tail(string text)
        {
            return text.Length > MaxBefore ? text.Substring(text.Length - MaxBefore) : text;
        }

        static bool ReadTextPattern(TextPattern text, Snapshot snapshot, string value)
        {
            var selections = text.GetSelection();
            if (selections == null || selections.Length == 0) return false;
            var caret = selections[0];
            snapshot.Caret = caret;
            string selected = caret.GetText(2);
            snapshot.HasSelection = selected.Length > 0;
            var whole = text.DocumentRange;

            var before = caret.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
            before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -MaxBefore);
            // Moving back can leave the field and pick up the rest of the page in Chrome.
            if (before.CompareEndpoints(TextPatternRangeEndpoint.Start, whole, TextPatternRangeEndpoint.Start) < 0)
                before.MoveEndpointByRange(TextPatternRangeEndpoint.Start, whole, TextPatternRangeEndpoint.Start);
            snapshot.Before = before.GetText(MaxBefore + 1);
            // Text from outside the field must never count as typed, so the value wins any mismatch.
            if (value != null && !value.Contains(snapshot.Before)) snapshot.Before = Tail(value);

            string all = value ?? whole.GetText(MaxCheck + 1);
            if (all.Length <= MaxCheck)
            {
                snapshot.CheckText = all;
                snapshot.CheckRange = whole;
            }
            else
            {
                var paragraph = caret.Clone();
                paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                string p = paragraph.GetText(MaxCheck + 1);
                if (p.Length <= MaxCheck)
                {
                    snapshot.CheckText = p;
                    snapshot.CheckRange = paragraph;
                }
            }

            snapshot.CaretRect = CaretRect(caret, snapshot.ElementRect);
            return true;
        }

        static Rect CaretRect(TextPatternRange caret, Rect fallback)
        {
            var rects = caret.GetBoundingRectangles();
            if (rects != null && rects.Length > 0) return rects[0];
            // An empty range often has no rectangle, so measure the character before it.
            var previous = caret.Clone();
            previous.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
            rects = previous.GetBoundingRectangles();
            return rects != null && rects.Length > 0 ? rects[rects.Length - 1] : fallback;
        }

        /// <summary>Text shown above the input box, oldest first: the likely conversation.</summary>
        public static string ReadScreenAbove(Snapshot snapshot)
        {
            var root = AutomationElement.FromHandle(snapshot.Window);
            var found = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var lines = new List<KeyValuePair<double, string>>();
            double top = snapshot.ElementRect.Top;
            double left = snapshot.ElementRect.Left - 60;
            double right = snapshot.ElementRect.Right + 60;
            foreach (AutomationElement item in found)
            {
                try
                {
                    var current = item.Current;
                    var rect = current.BoundingRectangle;
                    string name = current.Name;
                    if (rect.IsEmpty || string.IsNullOrWhiteSpace(name)) continue;
                    if (rect.Bottom > top || rect.Right < left || rect.Left > right) continue;
                    lines.Add(new KeyValuePair<double, string>(rect.Top, name.Trim()));
                }
                catch (ElementNotAvailableException) { }
            }
            var ordered = lines.OrderBy(l => l.Key).Select(l => l.Value).ToList();
            var recent = ordered.Skip(Math.Max(0, ordered.Count - MaxScreenLines));
            string joined = string.Join("\n", recent);
            return joined.Length > MaxScreenChars ? joined.Substring(joined.Length - MaxScreenChars) : joined;
        }
    }
}
