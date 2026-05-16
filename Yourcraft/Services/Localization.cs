namespace Yourcraft.Services;

public static class Localization
{
    public const string Chinese = "zh";
    public const string English = "en";

    public static string CurrentLanguage { get; set; } = Chinese;

    public static bool IsEnglish =>
        string.Equals(Normalize(CurrentLanguage), English, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Chinese;

    public static string T(string chinese, string english) => IsEnglish ? english : chinese;

    public static string DisplayName(string? language) =>
        string.Equals(Normalize(language), English, StringComparison.OrdinalIgnoreCase)
            ? "English"
            : "中文";
}
