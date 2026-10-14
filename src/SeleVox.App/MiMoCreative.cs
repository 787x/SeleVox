using System.Text;
using SeleVox.Core;

namespace SeleVox.App;

internal enum MiMoCreativeMode { PresetVoice, VoiceDesign, VoiceClone }
internal enum MiMoControl { NaturalLanguage, Director, StyleTags, AudioTags, Singing }

internal static class MiMoCreative
{
    internal static readonly string[] AudioTags = ["深呼吸", "吸气", "咳嗽", "叹气", "小声", "高呼", "语速加快", "语速放缓", "停顿", "沉默片刻", "中英混合"];
    internal static string Director(string role, string scene, string direction) => string.Join("\n", new[] {
        string.IsNullOrWhiteSpace(role) ? null : "角色：" + role.Trim(),
        string.IsNullOrWhiteSpace(scene) ? null : "场景：" + scene.Trim(),
        string.IsNullOrWhiteSpace(direction) ? null : "指导：" + direction.Trim() }.Where(s => s is not null));
    internal static string Model(MiMoCreativeMode mode) => mode switch {
        MiMoCreativeMode.VoiceDesign => "mimo-v2.5-tts-voicedesign",
        MiMoCreativeMode.VoiceClone => "mimo-v2.5-tts-voiceclone", _ => "mimo-v2.5-tts" };
}

internal sealed partial class MiMoSpeech
{
    internal static string CreativePrefix(Settings s) => s.MiMoModel is "mimo-v2.5-tts-voicedesign" or "mimo-v2.5-tts-voiceclone"
        ? "" : MiMoStyleTags.GetPrefix(s.MiMoOverallStyles);

    internal static IReadOnlyList<string> SplitCreativeText(string text, Settings s, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > HttpSpeech.MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new UserFeedbackException("播报文本为空、超过 32768 字符或包含非法控制字符。");
        if (s.MiMoSegmentCharacters is < 50 or > 3000) throw new UserFeedbackException("单段上限请设置为 50–3000 字符。");
        var budget = GetTextBudget(CreativePrefix(s));
        if (!s.MiMoAutoSegment)
        {
            if (text.Length > s.MiMoSegmentCharacters || Encoding.UTF8.GetByteCount(text) > budget)
                throw new UserFeedbackException("文本超过单段字符或 UTF-8 安全预算，请启用自动分段或缩短文本。");
            return [text];
        }
        return HttpSpeech.SplitText(text, budget, true, cancellation, s.MiMoSegmentCharacters).ToArray();
    }

    internal static object CreateCreativeRequest(string text, Settings s, string style)
    {
        if (s.MiMoModel == "mimo-v2.5-tts-voicedesign")
            return new { model = s.MiMoModel, stream = false, messages = new object[] {
                new { role = "user", content = style }, new { role = "assistant", content = text } }, audio = new { format = "wav" } };
        if (s.MiMoModel == "mimo-v2.5-tts-voiceclone")
        {
            if (s.MiMoReferenceAudio is not { } reference ||
                !(reference.StartsWith("data:audio/wav;base64,", StringComparison.Ordinal) || reference.StartsWith("data:audio/mpeg;base64,", StringComparison.Ordinal)) ||
                reference.Length > CloneAudio.MaxEncodedBytes)
                throw new UserFeedbackException("请选择有效的本地克隆样本（WAV / MP3，5–30 秒，Base64 不超过 10 MB）。");
            return CreateRequest(text, reference, style, s.MiMoModel);
        }
        return CreateRequest(text, s.MiMoVoice, style, s.MiMoModel);
    }
}
