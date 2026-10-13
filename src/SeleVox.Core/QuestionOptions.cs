namespace SeleVox.Core;

public enum AnswerReadingStrategy { Complete, Incremental }
public enum ThinkingProtocol { None, OpenAi, MiMo }
public enum ThinkingOption { Default, Disabled, Low, Medium, High, Enabled }
public static class ThinkingSettings
{
    public static bool IsValid(ThinkingProtocol protocol, ThinkingOption option) =>
        Enum.IsDefined(protocol) && Enum.IsDefined(option) && (protocol switch
        {
            ThinkingProtocol.None => option == ThinkingOption.Default,
            ThinkingProtocol.OpenAi => option is ThinkingOption.Default or ThinkingOption.Disabled or ThinkingOption.Low or ThinkingOption.Medium or ThinkingOption.High,
            ThinkingProtocol.MiMo => option is ThinkingOption.Default or ThinkingOption.Disabled or ThinkingOption.Enabled,
            _ => false
        });
}
public sealed record PromptPreset(string Id, string Name, string Prompt)
{
    public override string ToString() => Name;
}
public static class PromptPresets
{
    public static IReadOnlyList<PromptPreset> BuiltIn { get; } = Array.AsReadOnly(new[]
    {
        new PromptPreset("quick", "快速问答", Settings.DefaultLlmPrompt + "优先给结论，回答简短直接。"),
        new PromptPreset("explain", "详细解释", "你是耐心的教学助手。先说明核心概念，再分步解释原因，结合具体例子。明确区分事实和推测。"),
        new PromptPreset("translate", "翻译", "准确翻译用户输入。中文译为英文，其他语言译为中文。保留原意、语气和结构，尽量不附加说明。"),
        new PromptPreset("summarize", "文本总结", "总结用户提供的文本，提取核心观点、关键事实及行动项，组织成简明结构。不添加原文没有的信息。")
    });
    public static bool IsValid(PromptPreset[]? presets) => presets is null ||
        presets.Length <= 50 && presets.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && p.Id.Length <= 100 &&
            !BuiltIn.Any(b => b.Id == p.Id) && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 100 && p.Prompt is { Length: <= 8192 }) &&
        presets.Select(p => p.Id).Distinct().Count() == presets.Length;
}
