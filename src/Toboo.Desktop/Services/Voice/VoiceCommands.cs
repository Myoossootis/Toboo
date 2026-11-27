namespace Toboo.Desktop.Services;

internal enum VoiceCommand { None, DisableWake, EndConversation, StartFollow, StopFollow }

internal static class VoiceCommands
{
    private static readonly string[] StartVerbs = ["打开", "开启", "启动", "开始", "启用"];
    private static readonly string[] StopVerbs = ["关闭", "关掉", "关了", "关", "停止", "停用", "停掉", "停", "结束", "退出", "暂停"];
    private static readonly string[] PolitePrefixes = ["麻烦你帮我", "麻烦你", "麻烦", "请帮我", "请你", "帮我", "请"];
    private static readonly Dictionary<string, VoiceCommand> Phrases = BuildPhrases();

    internal static VoiceCommand Match(string text) => Phrases.GetValueOrDefault(CommandText(text));

    internal static bool SupportsCorrection(string original, string corrected, VoiceCommand command)
    {
        var source = CommandText(original);
        var target = CommandText(corrected);
        var verbs = command == VoiceCommand.StartFollow ? StartVerbs : StopVerbs;
        // Require the original utterance to express the same action, even when the model fixes the subject.
        return verbs.Any(verb => source.StartsWith(verb, StringComparison.Ordinal)) && IsWithinOneEdit(source, target);
    }

    internal static string FollowText(VoiceCommand command) =>
        command == VoiceCommand.StartFollow ? "打开摄像机跟随" : "关闭摄像机跟随";

    private static string CommandText(string text)
    {
        var phrase = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).TrimEnd('。', '！', '？', '.', '!', '?', '，', ',');
        var prefix = PolitePrefixes.FirstOrDefault(prefix => phrase.StartsWith(prefix, StringComparison.Ordinal));
        if (prefix is not null) phrase = phrase[prefix.Length..].TrimStart('，', ',');
        if (phrase.EndsWith('吧')) phrase = phrase[..^1];
        if (phrase.EndsWith("一下", StringComparison.Ordinal)) phrase = phrase[..^2];
        return phrase;
    }

    private static bool IsWithinOneEdit(string source, string target)
    {
        if (source.Length == target.Length)
            return source.Zip(target).Count(chars => chars.First != chars.Second) <= 1;
        if (source.Length < target.Length) (source, target) = (target, source);
        if (source.Length != target.Length + 1) return false;
        var index = 0;
        while (index < target.Length && source[index] == target[index]) index++;
        return source.AsSpan(index + 1).SequenceEqual(target.AsSpan(index));
    }

    private static Dictionary<string, VoiceCommand> BuildPhrases()
    {
        var phrases = new Dictionary<string, VoiceCommand>(StringComparer.Ordinal);
        void Add(VoiceCommand command, string[] verbs, string[] subjects)
        {
            foreach (var verb in verbs)
                foreach (var subject in subjects)
                    phrases.Add(verb + subject, command);
        }

        string[] followSubjects = ["跟随", "跟踪", "实物跟随", "人脸跟随", "人脸跟踪", "云台跟随", "云台跟踪",
            "摄像机跟随", "摄像机跟踪", "摄像机跟谁", "摄像机根随",
            "摄像头跟随", "摄像头跟踪", "摄像头跟谁", "摄像头根随"];
        Add(VoiceCommand.StartFollow, StartVerbs, followSubjects);
        Add(VoiceCommand.StopFollow, StopVerbs, followSubjects);
        Add(VoiceCommand.DisableWake, ["关闭", "关掉", "关了", "关", "停用", "停止", "关闭掉"],
            ["语音唤醒", "语音唤醒功能", "唤醒功能", "唤醒监听", "语音监听"]);
        Add(VoiceCommand.DisableWake, ["关闭", "关掉", "关了"], ["语音"]);
        Add(VoiceCommand.EndConversation, ["结束", "停止", "退出", "暂停"], ["语音对话", "语音会话", "语音聊天", "连续对话"]);
        phrases.Add("停止语音", VoiceCommand.EndConversation);
        phrases.Add("回到待机", VoiceCommand.EndConversation);
        return phrases;
    }
}
