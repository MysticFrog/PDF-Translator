namespace PdfTranslator.Core;

public sealed record Language(string Code, string Name)
{
    public override string ToString() => Code == "auto" ? Name : $"{Name} ({Code})";
}

public static class Languages
{
    public const string Auto = "auto";

    public static readonly IReadOnlyList<Language> All = new[]
    {
        new Language("auto", "Detect automatically"),
        new Language("en", "English"),
        new Language("de", "German"),
        new Language("fr", "French"),
        new Language("es", "Spanish"),
        new Language("it", "Italian"),
        new Language("pt", "Portuguese"),
        new Language("nl", "Dutch"),
        new Language("pl", "Polish"),
        new Language("cs", "Czech"),
        new Language("sk", "Slovak"),
        new Language("hu", "Hungarian"),
        new Language("ro", "Romanian"),
        new Language("bg", "Bulgarian"),
        new Language("el", "Greek"),
        new Language("ru", "Russian"),
        new Language("uk", "Ukrainian"),
        new Language("sv", "Swedish"),
        new Language("da", "Danish"),
        new Language("nb", "Norwegian"),
        new Language("fi", "Finnish"),
        new Language("tr", "Turkish"),
        new Language("ar", "Arabic"),
        new Language("he", "Hebrew"),
        new Language("hi", "Hindi"),
        new Language("th", "Thai"),
        new Language("vi", "Vietnamese"),
        new Language("id", "Indonesian"),
        new Language("ja", "Japanese"),
        new Language("ko", "Korean"),
        new Language("zh-CN", "Chinese (Simplified)"),
        new Language("zh-TW", "Chinese (Traditional)"),
    };

    public static IEnumerable<Language> Targets => All.Where(l => l.Code != Auto);

    public static string NameOf(string code) => All.FirstOrDefault(l => l.Code == code)?.Name ?? code;

    public static bool IsCjk(string code) => code is "ja" or "ko" or "zh-CN" or "zh-TW";

    /// <summary>Windows font family that has glyphs for the target language.</summary>
    public static string? FontFor(string code) => code switch
    {
        "zh-CN" => "Microsoft YaHei",
        "zh-TW" => "Microsoft JhengHei",
        "ja" => "MS Gothic",
        "ko" => "Malgun Gothic",
        "hi" => "Nirmala UI",
        "th" => "Tahoma",
        _ => null,
    };
}
