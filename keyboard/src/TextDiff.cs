// Works out which words a grammar fix changed, so the strip can mark just those.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ErrChatKeyboard
{
    public class Piece
    {
        public string Text;
        public bool Changed;
    }

    public static class TextDiff
    {
        const int MaxTableCells = 250000;
        // A rewrite changes nearly every word, and marking them all would just be noise.
        const double MaxChangedShare = 0.6;

        static string[] Words(string text)
        {
            return text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>The corrected text on one line, with words not in the original marked as changed.</summary>
        public static List<Piece> Compare(string original, string corrected)
        {
            var a = Words(original);
            var b = Words(corrected);
            string plain = string.Join(" ", b);
            if (a.Length * (long)b.Length > MaxTableCells) return Whole(plain);

            // Longest common subsequence of words, filled in from the end.
            var best = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    best[i, j] = a[i] == b[j] ? best[i + 1, j + 1] + 1 : Math.Max(best[i + 1, j], best[i, j + 1]);

            var pieces = new List<Piece>();
            int x = 0;
            for (int y = 0; y < b.Length; y++)
            {
                while (x < a.Length && a[x] != b[y] && best[x + 1, y] >= best[x, y + 1]) x++;
                bool same = x < a.Length && a[x] == b[y];
                if (same) x++;
                if (y > 0) Add(pieces, " ", false);
                Add(pieces, b[y], !same);
            }
            int changed = pieces.Where(p => p.Changed).Sum(p => p.Text.Length);
            return changed > plain.Length * MaxChangedShare ? Whole(plain) : pieces;
        }

        static List<Piece> Whole(string text)
        {
            return new List<Piece> { new Piece { Text = text } };
        }

        static void Add(List<Piece> pieces, string text, bool changed)
        {
            // Changed words stay separate so the underline breaks between them.
            if (pieces.Count > 0 && !changed && !pieces[pieces.Count - 1].Changed) pieces[pieces.Count - 1].Text += text;
            else pieces.Add(new Piece { Text = text, Changed = changed });
        }

        /// <summary>Cuts long text down to size, keeping the first change in view.</summary>
        public static List<Piece> Fit(List<Piece> pieces, int maxChars)
        {
            int total = pieces.Sum(p => p.Text.Length);
            if (total <= maxChars) return pieces;

            int firstChange = 0, position = 0;
            foreach (var piece in pieces)
            {
                if (piece.Changed) { firstChange = position; break; }
                position += piece.Text.Length;
            }
            int start = Math.Min(Math.Max(0, firstChange - 12), total - maxChars);
            int end = start + maxChars;

            var result = new List<Piece>();
            if (start > 0) Add(result, "…", false);
            position = 0;
            foreach (var piece in pieces)
            {
                int from = Math.Max(start, position), to = Math.Min(end, position + piece.Text.Length);
                if (from < to) Add(result, piece.Text.Substring(from - position, to - from), piece.Changed);
                position += piece.Text.Length;
            }
            if (end < total) Add(result, "…", false);
            return result;
        }

        public static string Join(List<Piece> pieces)
        {
            var text = new StringBuilder();
            foreach (var piece in pieces) text.Append(piece.Text);
            return text.ToString();
        }
    }
}
