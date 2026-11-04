// Works out the chat on screen: who said what, and whether the last word was theirs.
// WhatsApp Web barely labels its messages for UI Automation, but its text lines can
// be walked with their positions: their messages sit left, yours sit right.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Text;

namespace ErrChatKeyboard
{
    public class ScreenLine
    {
        public string Text;
        public double Left, Right, Top;
    }

    public class ChatText
    {
        public string Text = "";
        public string Key = "";       // changes when the latest messages change
        public string Latest = "";    // the newest message, which the replies should answer
        public bool LastIsMine;
        public int Theirs, Mine;
    }

    public static class Conversation
    {
        const int WalkBackChars = 2500;
        const int MaxLines = 60;
        const int MaxChars = 3000;
        const int KeyChars = 400;
        const double SideZone = 0.2; // the outer fifth of the chat's width on each side

        static readonly Regex TimeOnly = new Regex(@"^\d{1,2}[:.]\d{2}(\s?[ap]\.?m\.?)?$", RegexOptions.IgnoreCase);

        /// <summary>Labels lines by side, and drops timestamps and centered system text.</summary>
        public static ChatText Build(IEnumerable<ScreenLine> lines, double inputLeft, double inputRight)
        {
            double width = inputRight - inputLeft;
            var messages = new List<KeyValuePair<bool, string>>();
            bool messageEnded = true;
            foreach (var line in lines.OrderBy(l => l.Top))
            {
                string text = (line.Text ?? "").Trim();
                // Chat apps end each message with its time, which tells two messages in a
                // row apart. Without it, "bring the cake?" and "send the address?" read as one.
                if (TimeOnly.IsMatch(text)) { messageEnded = true; continue; }
                if (text.Length <= 1) continue;
                bool theirs = line.Left < inputLeft + width * SideZone;
                bool mine = line.Right > inputRight - width * SideZone;
                if (theirs == mine) continue; // centered, or a full-width banner
                // A wrapped message comes as several lines; put it back together.
                if (!messageEnded && messages.Count > 0 && messages[messages.Count - 1].Key == mine)
                    messages[messages.Count - 1] = new KeyValuePair<bool, string>(mine, messages[messages.Count - 1].Value + "\n" + text);
                else
                    messages.Add(new KeyValuePair<bool, string>(mine, text));
                messageEnded = false;
            }
            var result = new ChatText();
            if (messages.Count == 0) return result;
            var sb = new StringBuilder();
            foreach (var m in messages) sb.Append(m.Key ? "Me: " : "Them: ").Append(m.Value).Append('\n');
            string all = sb.ToString().TrimEnd();
            result.Text = all.Length > MaxChars ? all.Substring(all.Length - MaxChars) : all;
            result.Key = all.Length > KeyChars ? all.Substring(all.Length - KeyChars) : all;
            result.LastIsMine = messages[messages.Count - 1].Key;
            var newest = messages.LastOrDefault(m => !m.Key);
            result.Latest = newest.Value ?? "";
            result.Theirs = messages.Count(m => !m.Key);
            result.Mine = messages.Count(m => m.Key);
            return result;
        }

        /// <summary>Walks the text lines before the given range, inside the box's width and above it.</summary>
        public static List<ScreenLine> WalkLines(TextPatternRange from, Rect input)
        {
            var lines = new List<ScreenLine>();
            var back = from.Clone();
            back.MoveEndpointByRange(TextPatternRangeEndpoint.End, from, TextPatternRangeEndpoint.Start);
            back.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -WalkBackChars);
            var line = back.Clone();
            line.MoveEndpointByRange(TextPatternRangeEndpoint.End, back, TextPatternRangeEndpoint.Start);
            double left = input.Left - 60, right = input.Right + 60;
            for (int i = 0; i < MaxLines * 4 && lines.Count < MaxLines; i++)
            {
                line.ExpandToEnclosingUnit(TextUnit.Line);
                if (line.CompareEndpoints(TextPatternRangeEndpoint.End, from, TextPatternRangeEndpoint.Start) > 0) break;
                var rects = line.GetBoundingRectangles();
                if (rects != null && rects.Length > 0)
                {
                    var rect = rects[0];
                    if (rect.Bottom <= input.Top && rect.Right >= left && rect.Left <= right)
                        lines.Add(new ScreenLine { Text = line.GetText(500), Left = rect.Left, Right = rect.Right, Top = rect.Top });
                }
                if (line.Move(TextUnit.Line, 1) == 0) break;
            }
            return lines;
        }

        /// <summary>The chat around the focused box, or null if none is readable.</summary>
        public static ChatText Read(Snapshot snapshot)
        {
            var box = snapshot.ElementRect;
            if (snapshot.Caret != null)
            {
                var chat = Build(WalkLines(snapshot.Caret, box), box.Left, box.Right);
                if (chat.Text.Length > 0) return chat;
            }
            // Apps without page-wide text ranges: fall back to labelled text above the box.
            string screen = FocusWatcher.ReadScreenAbove(snapshot);
            if (string.IsNullOrWhiteSpace(screen)) return null;
            return new ChatText { Text = screen, Key = screen.Length > KeyChars ? screen.Substring(screen.Length - KeyChars) : screen };
        }
    }
}
