using System;
using System.Globalization;
using System.Text;

namespace KotH.Client.Menu
{
    /// <summary>Lightweight fuzzy server-name matcher: normalises accents and punctuation, matches every
    /// query word in any order via exact/prefix/substring, length-scaled Damerau-Levenshtein typos
    /// (0 for 1-3 chars, 1 for 4-7, 2 for 8+) and acronym/word-subsequence fallback.</summary>
    public static class NameMatch
    {
        const int MaxWord = 64;

        /// <summary>Match score in [0, 1] for query against a server name (0 = no match, 1 = exact).</summary>
        public static float Score(string query, string target)
        {
            string qNorm = Normalize(query);
            if (qNorm.Length == 0)
                return 1f;
            string tNorm = Normalize(target);
            if (tNorm.Length == 0)
                return 0f;

            string[] qWords = qNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] tWords = tNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string tCompact = StripSpaces(tNorm);
            string initials = Initials(tWords);

            float sum = 0f;
            foreach (string q in qWords)
            {
                float s = WordScore(q, tNorm, tCompact, tWords, initials);
                if (s <= 0f)
                    return 0f;
                sum += s;
            }
            return sum / qWords.Length;
        }

        static float WordScore(string q, string tNorm, string tCompact, string[] tWords, string initials)
        {
            float best = 0f;
            foreach (string w in tWords)
            {
                if (w == q)
                    return 1.00f;
                if (w.StartsWith(q, StringComparison.Ordinal))
                    best = MathF.Max(best, 0.92f);
            }
            if (best > 0f)
                return best;
            if (tNorm.Contains(q, StringComparison.Ordinal) || tCompact.Contains(q, StringComparison.Ordinal))
                return 0.85f;

            int budget = q.Length <= 3 ? 0 : q.Length <= 7 ? 1 : 2;
            if (budget > 0)
            {
                foreach (string w in tWords)
                {
                    int d = EditDistance(q, w, budget);
                    if (d <= budget)
                        best = MathF.Max(best, d == 1 ? 0.72f : 0.58f);
                    else if (w.Length > q.Length)
                    {
                        int dp = Math.Min(EditDistance(q, w.AsSpan(0, q.Length), budget),
                                          EditDistance(q, w.AsSpan(0, Math.Min(w.Length, q.Length + 1)), budget));
                        if (dp <= budget)
                            best = MathF.Max(best, (dp == 1 ? 0.72f : 0.58f) - 0.04f);
                    }
                }
                if (tWords.Length > 1 && Math.Abs(tCompact.Length - q.Length) <= budget)
                {
                    int dc = EditDistance(q, tCompact, budget);
                    if (dc <= budget)
                        best = MathF.Max(best, dc == 1 ? 0.72f : 0.58f);
                }
                if (best > 0f)
                    return best;
            }

            if (q.Length >= 2 && initials.Contains(q, StringComparison.Ordinal))
                return 0.52f;
            if (q.Length >= 3)
                foreach (string w in tWords)
                    if (IsSubsequence(q, w))
                        return 0.52f;
            return 0f;
        }

        // Lowercase, strip combining accents (Café -> cafe, München -> munchen), ß -> ss, punctuation -> space.
        internal static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            string formD = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            bool space = true;
            foreach (char c in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                char lower = char.ToLowerInvariant(c);
                if (lower == 'ß')
                {
                    sb.Append("ss");
                    space = false;
                }
                else if (char.IsLetterOrDigit(lower))
                {
                    sb.Append(lower);
                    space = false;
                }
                else if (!space)
                {
                    sb.Append(' ');
                    space = true;
                }
            }
            if (sb.Length > 0 && sb[^1] == ' ')
                sb.Length--;
            return sb.ToString();
        }

        static string StripSpaces(string s) => s.IndexOf(' ') < 0 ? s : s.Replace(" ", "", StringComparison.Ordinal);

        static string Initials(string[] words)
        {
            if (words.Length < 2)
                return "";
            Span<char> buf = words.Length <= 32 ? stackalloc char[words.Length] : new char[words.Length];
            for (int i = 0; i < words.Length; i++)
                buf[i] = words[i][0];
            return new string(buf);
        }

        static bool IsSubsequence(ReadOnlySpan<char> needle, ReadOnlySpan<char> hay)
        {
            if (needle.Length > hay.Length)
                return false;
            int j = 0;
            for (int i = 0; i < hay.Length && j < needle.Length; i++)
                if (hay[i] == needle[j])
                    j++;
            return j == needle.Length;
        }

        // Optimal String Alignment (Damerau-Levenshtein) with stack-only rows and early cutoff past max.
        internal static int EditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max)
        {
            int n = a.Length, m = b.Length;
            if (Math.Abs(n - m) > max || n > MaxWord || m > MaxWord)
                return max + 1;
            int stride = m + 1;
            Span<int> prev2 = stackalloc int[stride];
            Span<int> prev1 = stackalloc int[stride];
            Span<int> curr = stackalloc int[stride];
            for (int j = 0; j <= m; j++)
                prev1[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                int rowMin = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(prev1[j] + 1, curr[j - 1] + 1), prev1[j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        v = Math.Min(v, prev2[j - 2] + 1);
                    curr[j] = v;
                    if (v < rowMin)
                        rowMin = v;
                }
                if (rowMin > max)
                    return max + 1;
                Span<int> tmp = prev2;
                prev2 = prev1;
                prev1 = curr;
                curr = tmp;
            }
            return prev1[m];
        }
    }
}
