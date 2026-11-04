// Phone-keyboard word suggestions: instant, offline, and no AI quota.
// Built-in common words plus words the user types, kept only on this PC.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ErrChatKeyboard
{
    public class Suggestion
    {
        public string Word;
        public int ReplaceChars; // how many typed characters this word replaces
    }

    public class Predictor
    {
        const int LearnedWeight = 400;
        const int LearnedPairWeight = 3000;
        const int BuiltInPairWeight = 2000;
        const int MaxLearnedWordLength = 24;

        readonly Dictionary<string, double> builtIn = new Dictionary<string, double>();
        readonly Dictionary<string, string[]> next = new Dictionary<string, string[]>();
        readonly Dictionary<string, string> withApostrophe = new Dictionary<string, string>();
        readonly Dictionary<string, int> learned = new Dictionary<string, int>();
        readonly Dictionary<string, int> learnedPairs = new Dictionary<string, int>();
        readonly string file;
        public bool Dirty { get; private set; }

        public Predictor(string learnedFile)
        {
            file = learnedFile;
            LoadBuiltIn();
            LoadLearned();
        }

        static string Resource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        void LoadBuiltIn()
        {
            int rank = 0;
            foreach (var w in Resource("words.txt").Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (builtIn.ContainsKey(w)) continue;
                builtIn[w] = 100000.0 / (rank + 10);
                rank++;
                if (w.Contains("'")) withApostrophe[w.Replace("'", "")] = w;
            }
            foreach (var line in Resource("next.txt").Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line.Substring(0, colon).Trim();
                var words = line.Substring(colon + 1).Split(new[] { ' ', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (!next.ContainsKey(key)) next[key] = words;
            }
        }

        void LoadLearned()
        {
            if (file == null || !File.Exists(file)) return;
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var parts = line.Split('\t');
                    int count;
                    if (parts.Length == 3 && parts[0] == "w" && int.TryParse(parts[2], out count)) learned[parts[1]] = count;
                    else if (parts.Length == 4 && parts[0] == "p" && int.TryParse(parts[3], out count)) learnedPairs[parts[1] + " " + parts[2]] = count;
                }
            }
            catch (IOException) { }
        }

        public void Save()
        {
            if (file == null || !Dirty) return;
            var sb = new StringBuilder();
            foreach (var kv in learned) sb.Append("w\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            foreach (var kv in learnedPairs)
            {
                var pair = kv.Key.Split(' ');
                sb.Append("p\t").Append(pair[0]).Append('\t').Append(pair[1]).Append('\t').Append(kv.Value).Append('\n');
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file + ".tmp", sb.ToString(), Encoding.UTF8);
                if (File.Exists(file)) File.Delete(file);
                File.Move(file + ".tmp", file);
                Dirty = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void ClearLearned()
        {
            learned.Clear();
            learnedPairs.Clear();
            Dirty = false;
            try { if (file != null && File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
        }

        static bool IsWordChar(char c)
        {
            return char.IsLetter(c) || c == '\'' || c == '’';
        }

        static bool IsSentenceEnd(char c)
        {
            return c == '.' || c == '!' || c == '?' || c == '\n' || c == '\r';
        }

        /// <summary>Splits the text before the caret into previous word and the word being typed.</summary>
        public static void Split(string before, out string previous, out string partial, out bool sentenceStart)
        {
            int i = before.Length;
            while (i > 0 && IsWordChar(before[i - 1])) i--;
            partial = before.Substring(i);
            int j = i;
            while (j > 0 && (before[j - 1] == ' ' || before[j - 1] == '\t')) j--;
            sentenceStart = j == 0 || IsSentenceEnd(before[j - 1]);
            // Stops at once on a comma or number: words around those aren't really a pair.
            int end = j;
            while (j > 0 && IsWordChar(before[j - 1])) j--;
            previous = before.Substring(j, end - j);
        }

        static string Normalize(string word)
        {
            return word.Replace('’', '\'').ToLowerInvariant();
        }

        static int Distance(string a, string b)
        {
            // Damerau-Levenshtein, so swapped letters ("teh") count as one mistake.
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
            return d[a.Length, b.Length];
        }

        double Score(string word, string previous)
        {
            double score;
            builtIn.TryGetValue(word, out score);
            int count;
            if (learned.TryGetValue(word, out count)) score += count * LearnedWeight;
            if (previous.Length > 0)
            {
                if (learnedPairs.TryGetValue(previous + " " + word, out count)) score += count * LearnedPairWeight;
                string[] follows;
                if (next.TryGetValue(previous, out follows))
                {
                    int index = Array.IndexOf(follows, word);
                    if (index >= 0) score += BuiltInPairWeight * (follows.Length - index);
                }
            }
            return score;
        }

        IEnumerable<string> Vocabulary()
        {
            return builtIn.Keys.Concat(learned.Keys).Distinct();
        }

        static string MatchCase(string word, string typed, bool sentenceStart)
        {
            if (word == "i" || word.StartsWith("i'")) return "I" + word.Substring(1);
            if (typed.Length > 1 && typed.All(c => !char.IsLetter(c) || char.IsUpper(c))) return word.ToUpperInvariant();
            bool capital = typed.Length > 0 ? char.IsUpper(typed[0]) : sentenceStart;
            if (capital && word.Length > 0) return char.ToUpperInvariant(word[0]) + word.Substring(1);
            return word;
        }

        public List<Suggestion> Predict(string before, int count)
        {
            string previousRaw, partialRaw;
            bool sentenceStart;
            Split(before ?? "", out previousRaw, out partialRaw, out sentenceStart);
            string previous = Normalize(previousRaw);
            string partial = Normalize(partialRaw).TrimEnd('\'');
            var result = new List<Suggestion>();

            if (partial.Length == 0)
            {
                foreach (var word in NextWords(previous, sentenceStart))
                {
                    if (result.Count == count) break;
                    result.Add(new Suggestion { Word = MatchCase(word, "", sentenceStart), ReplaceChars = 0 });
                }
                return result;
            }

            string plain = partial.Replace("'", "");
            bool known = builtIn.ContainsKey(partial) || learned.ContainsKey(partial);
            var exact = new List<string>();      // "dont" -> "don't", "i" -> "I"
            var completions = new List<string>();
            var typos = new List<string>();
            if (partialRaw == "i") exact.Add("i");
            foreach (var word in Vocabulary())
            {
                string candidate = word.Replace("'", "");
                if (word == partial) continue;
                if (candidate == plain) exact.Add(word);
                else if (candidate.StartsWith(plain, StringComparison.Ordinal)) completions.Add(word);
                // Typos: only when what was typed isn't a word itself.
                else if (!known && plain.Length >= 3 && Math.Abs(candidate.Length - plain.Length) <= 1 && Distance(candidate, plain) == 1) typos.Add(word);
            }
            Func<string, double> score = w => Score(w, previous);
            // A completion always beats a typo fix, so "hel" offers "hello" before "he".
            var ordered = exact.Concat(completions.OrderByDescending(score).ThenBy(w => w, StringComparer.Ordinal))
                .Concat(typos.OrderByDescending(score).ThenBy(w => w, StringComparer.Ordinal));
            foreach (var candidate in ordered)
            {
                if (result.Count == count) break;
                string word = MatchCase(candidate, partialRaw, sentenceStart);
                if (result.Any(r => r.Word == word)) continue;
                result.Add(new Suggestion { Word = word, ReplaceChars = partialRaw.Length });
            }
            return result;
        }

        IEnumerable<string> NextWords(string previous, bool sentenceStart)
        {
            var seen = new HashSet<string>();
            var pairs = learnedPairs.Where(p => p.Key.StartsWith(previous + " ", StringComparison.Ordinal))
                .OrderByDescending(p => p.Value).Select(p => p.Key.Substring(previous.Length + 1));
            if (previous.Length > 0 && !sentenceStart)
                foreach (var w in pairs) if (seen.Add(w)) yield return w;
            string[] follows;
            if (!sentenceStart && previous.Length > 0 && next.TryGetValue(previous, out follows))
                foreach (var w in follows) if (seen.Add(w)) yield return w;
            if (sentenceStart)
                foreach (var w in next["START"]) if (seen.Add(w.ToLowerInvariant())) yield return w.ToLowerInvariant();
            foreach (var w in next["ANY"]) if (seen.Add(w)) yield return w;
        }

        /// <summary>Learns from text the user just finished typing. Skips anything with digits.</summary>
        public void Learn(string typed)
        {
            string previous = "";
            foreach (var token in typed.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string cleaned = Normalize(token.Trim(',', '.', '!', '?', ';', ':', '"', '(', ')'));
                bool isWord = cleaned.Length >= 1 && cleaned.Length <= MaxLearnedWordLength && cleaned.All(IsWordChar);
                if (!isWord) { previous = ""; continue; }
                int count;
                learned.TryGetValue(cleaned, out count);
                learned[cleaned] = count + 1;
                if (previous.Length > 0)
                {
                    string key = previous + " " + cleaned;
                    learnedPairs.TryGetValue(key, out count);
                    learnedPairs[key] = count + 1;
                }
                previous = token.EndsWith(".") || token.EndsWith("!") || token.EndsWith("?") ? "" : cleaned;
                Dirty = true;
            }
        }
    }
}
