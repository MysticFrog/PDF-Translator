using System.Reflection;

namespace PdfTranslator;

/// <summary>Product name and the version stamped by GitVersion.targets (version.commits+hash[.dirty]).</summary>
public static class AppInfo
{
    public const string Name = "PDF Translator";

    public static string Copyright { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    public const string RepositoryUrl = "https://github.com/MysticFrog/PDF-TRANSLATOR";

    /// <summary>Licences of the bundled components, installed next to the program.</summary>
    public static string NoticesFile => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>"PDF Translator 1.2.0.5 (9a1ce70)".</summary>
    public static string Title
    {
        get
        {
            var parts = Version.Split('+', 2);
            return parts.Length == 2 ? $"{Name} {parts[0]} ({parts[1]})" : $"{Name} {Version}";
        }
    }
}
