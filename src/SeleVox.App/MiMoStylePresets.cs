namespace SeleVox.App;

internal sealed record MiMoStyleCategory(string Name, string[] Styles);

internal static class MiMoStylePresets
{
    internal static IReadOnlyList<MiMoStyleCategory> Categories { get; } =
    [
        new("基础情绪", ["开心", "悲伤", "愤怒", "恐惧", "惊讶", "兴奋", "委屈", "平静", "冷漠"]),
        new("复合情绪", ["怅然", "欣慰", "无奈", "愧疚", "释然", "嫉妒", "厌倦", "忐忑", "动情"]),
        new("整体语调", ["温柔", "高冷", "活泼", "严肃", "慵懒", "俏皮", "深沉", "干练", "凌厉"]),
        new("音色定位", ["磁性", "醇厚", "清亮", "空灵", "稚嫩", "苍老", "甜美", "沙哑", "醇雅"]),
        new("人设腔调", ["夹子音", "御姐音", "正太音", "大叔音", "台湾腔"]),
        new("方言", ["东北话", "四川话", "河南话", "粤语"]),
        new("角色扮演", ["孙悟空", "林黛玉"]),
        new("特殊风格", ["唱歌"])
    ];

    internal static IEnumerable<MiMoStyleCategory> Search(string query) => Categories
        .Select(c => new MiMoStyleCategory(c.Name, c.Styles.Where(s =>
            c.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
            s.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray()))
        .Where(c => c.Styles.Length > 0);
}
