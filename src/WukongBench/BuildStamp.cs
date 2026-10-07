using System.Reflection;

namespace WukongBench;

/// <summary>
/// Метка сборки, чтобы по логу сразу видно, какая версия запущена.
/// Без неё легко запустить старую копию и гадать, почему поведение не то.
/// </summary>
public static class BuildStamp
{
    public static string Version { get; } =
        typeof(BuildStamp).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static string Commit { get; } = ReadCommit();

    /// <summary>
    /// Хеш последнего коммита. При сборке через dotnet build он попадает в
    /// метаданные автоматически; если нет — остаётся прочерк.
    /// </summary>
    private static string ReadCommit()
    {
        var informational = typeof(BuildStamp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational)) return "commit неизвестен";

        // Информационная версия может быть вида "1.0.0+abc1234".
        var plus = informational.IndexOf('+');
        return plus >= 0 && plus + 1 < informational.Length
            ? informational[(plus + 1)..]
            : informational;
    }
}