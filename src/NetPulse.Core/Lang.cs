namespace NetPulse.Core;

public enum UiLanguage
{
    /// <summary>Chinese if the Windows display language is Chinese, otherwise English.</summary>
    Auto,
    Chinese,
    English,
}

/// <summary>
/// UI language, chosen once at start-up. Text is written inline as <c>T("中文", "English")</c>, so each
/// translation sits next to where it is used and nothing can go missing.
/// </summary>
public static class Lang
{
    public static bool IsEnglish { get; private set; }

    public static void Use(UiLanguage language) => IsEnglish = language switch
    {
        UiLanguage.Chinese => false,
        UiLanguage.English => true,
        _ => !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
    };

    public static string T(string chinese, string english) => IsEnglish ? english : chinese;
}
