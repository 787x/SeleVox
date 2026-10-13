using System.Text;

namespace SeleVox.Core;

// Keeps only the unfinished tail. Never splits surrogate pairs or combining text elements.
public sealed class AnswerSentenceBuffer
{
    public const int MaxSegmentLength = 512;
    private readonly StringBuilder pending = new();
    public IEnumerable<string> Append(string delta, bool final = false)
    {
        pending.Append(delta);
        while (pending.Length > 0)
        {
            var text = pending.ToString();
            var boundary = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '。' or '！' or '？' or '\n' ||
                    c is '!' or '?' || c == '.' && IsSentencePeriod(text, i, final))
                {
                    boundary = i + 1;
                    while (boundary < text.Length && "。！？.!?\"'”’）)".Contains(text[boundary])) boundary++;
                    break;
                }
            }
            if ((boundary == 0 || boundary > MaxSegmentLength) && text.Length >= MaxSegmentLength)
            {
                var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text);
                boundary = elements.LastOrDefault(i => i <= MaxSegmentLength && i > 0);
                for (var i = boundary - 1; i >= MaxSegmentLength / 2; i--)
                    if (char.IsWhiteSpace(text[i]) || text[i] is '，' or ',' or '；' or ';') { boundary = i + 1; break; }
            }
            if (boundary == 0 && final) boundary = text.Length;
            if (boundary == 0) yield break;
            // A high surrogate at the end of a delta must wait for its low surrogate.
            if (char.IsHighSurrogate(text[boundary - 1])) boundary--;
            if (boundary == 0) yield break;
            var sentence = text[..boundary].Trim(); pending.Remove(0, boundary);
            if (sentence.Length > 0 && sentence.Any(char.IsLetterOrDigit)) yield return sentence;
        }
    }
    private static bool IsSentencePeriod(string text, int index, bool final)
    {
        if (final && index + 1 == text.Length) return true;
        if (index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]) && !"\"'”’）)".Contains(text[index + 1])) return false;
        // Do not submit a decimal or common abbreviated title at a token boundary.
        if (index + 1 == text.Length && index > 0 && char.IsDigit(text[index - 1])) return false;
        var start = index;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.')) start--;
        var word = text[start..index].ToLowerInvariant();
        return word is not ("mr" or "mrs" or "ms" or "dr" or "prof" or "sr" or "jr" or "e.g" or "i.e") &&
            !(word.Length == 1 && word[0] is >= 'a' and <= 'z');
    }
}
