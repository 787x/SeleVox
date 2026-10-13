using System.Globalization;
using System.Text;

namespace SeleVox.Core;

/// <summary>Ordered overall tags, independent of MiMo's free-form user instruction.</summary>
public sealed class MiMoStyleTags
{
    public const int MaxNameLength = 64, MaxTags = 16;
    private readonly List<string> selected = [];
    public IReadOnlyList<string> Selected => selected.AsReadOnly();
    public MiMoStyleTags(IEnumerable<string>? names = null)
    {
        var values = names?.ToArray();
        if (!IsValid(values)) throw new UserFeedbackException("整体风格配置无效，请检查名称、重复项或唱歌组合。");
        if (values is not null) selected.AddRange(values);
    }

    public static bool IsValidName(string? name) => name is { Length: > 0 and <= MaxNameLength } &&
        name == name.Trim() && !string.IsNullOrWhiteSpace(name) &&
        !name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) && c != ' ' || "()（）[]【】".Contains(c)) &&
        // Reject unpaired UTF-16 surrogates and invisible format controls (e.g. bidi overrides).
        ValidUnicode(name);

    private static bool ValidUnicode(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]))
            {
                if (i + 1 == name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(name[i]) || char.GetUnicodeCategory(name[i]) == UnicodeCategory.Format) return false;
        }
        return name.EnumerateRunes().All(r => Rune.GetUnicodeCategory(r) != UnicodeCategory.Format);
    }

    public static bool IsValid(IReadOnlyList<string>? names) => names is null ||
        names.Count <= MaxTags && names.All(IsValidName) && names.Distinct(StringComparer.Ordinal).Count() == names.Count &&
        (names.Count <= 1 || !names.Any(IsSinging));

    // The official aliases must not bypass singing's exclusive-mode rule through custom input.
    public static bool IsSinging(string name) => name == "唱歌" || name.Equals("sing", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("singing", StringComparison.OrdinalIgnoreCase);

    public static string GetPrefix(IReadOnlyList<string>? names)
    {
        if (!IsValid(names)) throw new UserFeedbackException("整体风格配置无效：每项最多 64 字符、最多 16 项，不可包含换行、控制字符、括号或重复项；唱歌须单独使用。");
        return names is null || names.Count == 0 ? "" : "(" + string.Join(" ", names) + ")";
    }

    public void Add(string value)
    {
        // Validate before trimming so a trailing newline/control cannot silently become a tag.
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) && c != ' '))
            throw new UserFeedbackException("风格名称不可包含换行或控制字符。");
        var name = value.Trim();
        if (!IsValidName(name)) throw new UserFeedbackException("风格名称须为 1–64 字符，不可包含换行、控制字符或括号。");
        if (selected.Contains(name)) return;
        if (IsSinging(name) || selected.Any(IsSinging)) selected.Clear();
        if (selected.Count >= MaxTags) throw new UserFeedbackException("最多选择 16 个整体风格，请先移除一项。");
        selected.Add(name);
    }
    public void Remove(string name) => selected.Remove(name);
    public void Clear() => selected.Clear();
}
