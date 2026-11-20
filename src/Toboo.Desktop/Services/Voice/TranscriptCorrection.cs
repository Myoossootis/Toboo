using System.Text;
using System.Text.RegularExpressions;

namespace Toboo.Desktop.Services;

internal static class TranscriptCorrection
{
    public static string NormalizeTerms(string text)
    {
        text = text.Trim().Normalize(NormalizationForm.FormKC);
        foreach (var (pattern, replacement) in new[] {
            (@"\bdeep\s*seek\b", "DeepSeek"), (@"\bar\s*u\s*co\b", "ArUco"),
            (@"\besp\s*32\b", "ESP32"), (@"\bics\s*43432\b", "ICS43432"), (@"\bsg\s*90\b", "SG90") })
            text = Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }
    public static bool IsConservative(string original, string corrected)
    {
        if (string.IsNullOrWhiteSpace(corrected) || corrected.Length > 4000 ||
            corrected.Length > Math.Max(40, original.Length * 1.4) || corrected.Length < original.Length * 0.6) return false;
        static string[] Tokens(string text, string pattern) => Regex.Matches(text.Normalize(NormalizationForm.FormKC), pattern)
            .Select(m => m.Value).ToArray();
        return Tokens(original, @"\d+(?:\.\d+)?").SequenceEqual(Tokens(corrected, @"\d+(?:\.\d+)?"))
            && Tokens(original, "不要|不能|不准|没有|别|没|不|停止|取消|禁止")
                .SequenceEqual(Tokens(corrected, "不要|不能|不准|没有|别|没|不|停止|取消|禁止"));
    }
}
