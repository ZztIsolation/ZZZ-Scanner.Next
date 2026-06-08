using Microsoft.VisualBasic;

namespace ZZZScannerNext.Cleaning;

public static class StringMatcher
{
    public static string SimplifyChinese(string text, bool keepChineseAndDigits = false)
    {
        text ??= string.Empty;
        try
        {
            text = Strings.StrConv(text, VbStrConv.SimplifiedChinese) ?? text;
        }
        catch
        {
            // Conversion is a convenience for OCR noise; matching still works without it.
        }

        if (!keepChineseAndDigits)
        {
            return text.Trim();
        }

        return string.Concat(text.Where(c => IsChinese(c) || char.IsDigit(c))).Trim();
    }

    public static MatchResult BestMatch(IEnumerable<string> candidates, string text, float factor = 0.45f)
    {
        var normalizedText = NormalizeForDistance(text);
        var best = text;
        var bestDistance = normalizedText.Length == 0 ? int.MaxValue : normalizedText.Length;

        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var normalizedCandidate = NormalizeForDistance(candidate);
            var distance = LevenshteinDistance(normalizedText, normalizedCandidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        var maxLength = Math.Max(NormalizeForDistance(best).Length, normalizedText.Length);
        var score = maxLength == 0 ? 1f : 1f - (float)bestDistance / maxLength;
        return score >= factor ? new MatchResult(best, score) : new MatchResult(text, score);
    }

    public static string NumericToken(string text)
    {
        text = text.Replace('％', '%').Replace('O', '0').Replace('o', '0');
        return string.Concat(text.Where(c => char.IsDigit(c) || c is '.' or '%' or '-' or '+'));
    }

    private static string NormalizeForDistance(string text)
    {
        return text.Replace("％", "%", StringComparison.Ordinal)
            .Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static bool IsChinese(char c)
    {
        return c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF';
    }

    private static int LevenshteinDistance(string source, string target)
    {
        var n = source.Length;
        var m = target.Length;
        var d = new int[n + 1, m + 1];

        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        for (var i = 0; i <= n; d[i, 0] = i++)
        {
        }

        for (var j = 0; j <= m; d[0, j] = j++)
        {
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}

public readonly record struct MatchResult(string Text, float Score);
