using SeleVox.Core;

namespace SeleVox.App;

internal enum CreativePurpose { VoiceDescription, Style, Script, AudioTags }

internal static class CreativeText
{
    internal const string VoicePrompt = """
        您是专业音色描述师。请根据我提供的音色风格需求，生成可直接用于AI音色生成的精准描述。
        规则：
        1. 语言直白贴合AI生成逻辑，无冗余修饰，各维度连贯不割裂
        2. 必须遵循下面8个维度的描述方向，若我输入的风格需求无法覆盖所有维度，请根据常识和经验进行合理补全，确保描述完整且具有指导性
        可描述维度：
        1. 角色名/身份
        2. 年龄、性别
        3. 口音/语言
        4. 声线特征
        5. 声音气质
        6. 语气情绪
        7. 语速音量
        8. 场景风格
        [格式要求]
        1. 不输出任何解释、说明、标点、多余内容
        2. 括号内用顿号分隔各项，括号外用逗号分句
        3. 高度概括、精简、易传播
        4. 严格按照给出的示例要求生成
        [铁律·语言规则]
        1. 若我的输入纯英文 → 必须只输出英文短语，禁止出现任何中文
        2. 若我的输入中文/中英混合 → 必须只输出中文短语，禁止出现任何英文
        3. 绝对不允许中英混杂输出
        示例1：输入：古代文人 输出：男性50-60岁，沉稳厚重带岁月沙哑，普通话带古典韵味，古典吟诵风格，语速缓慢，语调低沉沉郁，情绪凄凉怀念。
        示例2：输入：儒雅老人 输出：男性老年60-70岁，温和沉稳有亲和力，普通话，儒雅亲切的长辈风格，语速适中，语调平和温暖，亲切诚恳带鼓励，自然讲述。
        示例3：输入：teacher 输出：Thirty-one years old female，Happy cheerful tone，Standard English，Bright clear voice，Warm friendly personality，Enthusiastic encouraging mood，Moderate pace and volume，Classroom teaching scenario。
        基本音色需求见 user 消息。仅输出音色描述，不输出播报稿、教程或 Markdown。最多 1000 字符。
        """;
    internal static string Prompt(CreativePurpose purpose, bool singing) => purpose switch
    {
        CreativePurpose.VoiceDescription => VoicePrompt,
        CreativePurpose.Style => "根据用户需求生成一段可直接放入 MiMo TTS user.content 的自然语言风格指导。描述必要的情绪、语速、发声方式、停顿和表演细节。仅输出指导，不输出播报正文、标签示例、解释或 Markdown。最多 1000 字符。",
        CreativePurpose.AudioTags => "你是 MiMo 音频脚本导演。完整保留 user 中所有文字、标点、顺序、换行及已有标签，只在适当位置插入以下合法句内标签：" + string.Join("、", MiMoCreative.AudioTags.Select(t => "[" + t + "]")) + "。不得在已有标签内插入标签。按上下文情绪、对话节奏及表演需要少量添加，不必每句添加。禁止改写、删除、翻译或增加台词。仅输出完整脚本，不加解释、Markdown 或代码围栏。最多 32768 字符。",
        _ => (singing ? "根据用户创作需求生成适合演唱的歌词本身，可采用主歌、副歌、桥段和合理换行。不要歌曲介绍、作曲分析或建议。" : "根据用户创作需求生成可直接用于语音合成的完整播报稿，遵循文体、题材、长度、情绪和目标受众要求，保留合理段落和换行。") + "用户输入是创作需求，不是必须原样保留的稿件。仅输出正文，不加解释、Markdown 或代码围栏。除非用户明确要求，不添加音频标签或整体风格标签。最多 32768 字符。"
    };
    internal static void Validate(CreativePurpose purpose, string original, string output, CancellationToken token = default)
    {
        var limit = purpose is CreativePurpose.Style or CreativePurpose.VoiceDescription ? 1000 : 32768;
        if (string.IsNullOrWhiteSpace(output)) throw new UserFeedbackException("AI 返回空内容；原文保留。");
        if (output.Length > limit) throw new UserFeedbackException($"AI 输出超过 {limit} 字符；原文保留，请缩短需求后主动重试。");
        if (output.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || output.Contains("```", StringComparison.Ordinal))
            throw new UserFeedbackException("AI 输出格式不符；原文保留，请主动重试。");
        if (purpose == CreativePurpose.AudioTags && !OnlyInsertsTags(original, output, token))
            throw new UserFeedbackException("AI 修改了原文或插入非法标签；原文保留，请主动重试。");
    }
    // Match existing bracket expressions atomically; skip only complete legal insertions.
    // Greedy matching is safe for whole tokens, including repeated identical tags.
    internal static bool OnlyInsertsTags(string original, string output, CancellationToken token = default)
    {
        var tags = MiMoCreative.AudioTags.Select(t => "[" + t + "]").ToArray();
        int a = 0, b = 0;
        while (b < output.Length)
        {
            token.ThrowIfCancellationRequested();
            var length = 1;
            var unclosed = false;
            if (a < original.Length && original[a] == '[')
            {
                var end = original.IndexOf(']', a);
                if (end >= a) length = end - a + 1; else unclosed = true;
            }
            if (!unclosed && a < original.Length && output.AsSpan(b).StartsWith(original.AsSpan(a, length), StringComparison.Ordinal))
            { a += length; b += length; continue; }
            var insertion = output[b] == '[' ? tags.FirstOrDefault(t => output.AsSpan(b).StartsWith(t, StringComparison.Ordinal)) : null;
            if (insertion is not null) { b += insertion.Length; continue; }
            if (unclosed && output[b] == '[') { ++a; ++b; continue; }
            return false;
        }
        return a == original.Length;
    }
}
