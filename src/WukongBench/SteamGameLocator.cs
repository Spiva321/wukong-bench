using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace WukongBench;

/// <summary>
/// Находит папку установки Benchmark Tool.
///
/// Три источника, в порядке убывания надёжности:
///   1. путь, переданный пользователем в командной строке;
///   2. реестр Windows — там Steam хранит свой путь;
///   3. стандартные места установки Steam, если реестр недоступен.
///
/// У одного Steam бывает несколько библиотек на разных дисках, поэтому читаем
/// steamapps\libraryfolders.vdf и проверяем их все.
/// </summary>
public sealed class SteamGameLocator
{
    public const string BenchmarkFolderName = "Black Myth Wukong Benchmark Tool";
    public const string ExecutableRelativePath = @"b1\Binaries\Win64\b1-Win64-Shipping.exe";
    public const string SettingsRelativePath = @"b1\Saved\Config\Windows\GameUserSettings.ini";
    public const string ResultsRelativePath = @"b1\BenchMarkHistory\Tool";

    public static readonly string[] ProcessNames = ["b1", "b1-Win64-Shipping", "b1_benchmark"];

    private readonly Action<string> _log;

    public SteamGameLocator(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>Путь к папке установки. Бросает исключение с понятным текстом, если не нашлось.</summary>
    public string FindInstallDir(string? userSuppliedPath = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(userSuppliedPath))
        {
            _log($"Путь из командной строки: {userSuppliedPath}");
            candidates.Add(userSuppliedPath);
        }

        foreach (var steam in SteamRoots())
        foreach (var library in EnumerateLibraries(steam))
            candidates.Add(Path.Combine(library, "steamapps", "common", BenchmarkFolderName));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Validate(candidate) is { } exe)
            {
                _log($"Найдена установка: {candidate}");
                _log($"  исполняемый файл: {exe}");
                return candidate;
            }
        }

        throw new FileNotFoundException(BuildNotFoundMessage(candidates));
    }

    /// <summary>Проверяет папку установки. Возвращает путь к .exe, если всё на месте, иначе null.</summary>
    public static string? Validate(string installDir)
    {
        var exe = Path.Combine(installDir, ExecutableRelativePath);
        if (!File.Exists(exe)) return null;

        var settings = Path.Combine(installDir, SettingsRelativePath);
        if (!File.Exists(settings)) return null;

        return exe;
    }

    public static string SettingsPath(string installDir) =>
        Path.Combine(installDir, SettingsRelativePath);

    /// <summary>Папка с JSON-результатами: %TEMP%\b1\BenchMarkHistory\Tool\</summary>
    public static string ResultsDirectory()
    {
        var temp = Environment.GetEnvironmentVariable("TEMP")
                   ?? Environment.GetEnvironmentVariable("TMP")
                   ?? Path.GetTempPath();

        return Path.Combine(temp, "b1", "BenchMarkHistory", "Tool");
    }

    private IEnumerable<string> SteamRoots()
    {
        var fromRegistry = FindSteamPath();
        if (fromRegistry is not null) yield return fromRegistry;

        foreach (var path in DefaultSteamLocations())
            yield return path;
    }

    /// <summary>Читает путь Steam из реестра. null, если ключа нет или мы не на Windows.</summary>
    private string? FindSteamPath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key is null) return null;

            // В разных версиях Steam путь лежит то отдельным ключом, то внутри SteamExe.
            if (key.GetValue("SteamPath") is string path && Directory.Exists(path))
                return path;

            if (key.GetValue("SteamExe") is string exe)
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log($"Не удалось прочитать реестр: {e.Message}. Пробую стандартные пути.");
        }

        return null;
    }

    private static IEnumerable<string> DefaultSteamLocations()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(folder)) continue;

            var path = Path.Combine(folder, "Steam");
            if (Directory.Exists(path)) yield return path;
        }
    }

    /// <summary>
    /// Возвращает папку Steam и все её библиотеки из steamapps\libraryfolders.vdf.
    /// Даже если vdf недоступен, отдаёт хотя бы корневую папку.
    /// </summary>
    public IEnumerable<string> EnumerateLibraries(string steamDir)
    {
        yield return steamDir;

        var vdfPath = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        string text;
        try { text = File.ReadAllText(vdfPath); }
        catch (IOException) { yield break; }

        // Формат vdf: "0" { "path" "D:\\SteamLibrary" ... }. Нас интересует только ключ path.
        foreach (Match match in Regex.Matches(text, @"""path""\s+""([^""]+)"""))
        {
            // В vdf обратные слэши удвоены: D:\\SteamLibrary → D:\SteamLibrary
            var path = match.Groups[1].Value.Replace("\\\\", "\\");

            if (!Directory.Exists(path)) continue;

            _log($"Библиотека Steam: {path}");
            yield return path;
        }
    }

    private string BuildNotFoundMessage(List<string> candidates)
    {
        var text = new StringBuilder();
        text.AppendLine("Не удалось найти установку Black Myth: Wukong Benchmark Tool.");
        text.AppendLine();
        text.AppendLine("Проверено:");
        if (candidates.Count == 0)
            text.AppendLine("  — ни одной папки Steam (реестр и стандартные места пусты)");
        else
            foreach (var candidate in candidates)
                text.AppendLine($"  — {candidate}");

        text.AppendLine();
        text.AppendLine("Что проверить:");
        text.AppendLine("  1. Бенчмарк установлен из Steam (бесплатное приложение, AppID 3132990).");
        text.AppendLine("  2. Его запускали вручную хотя бы один раз — иначе игра не создала конфиг настроек.");
        text.AppendLine("  3. Указать путь вручную первым аргументом:");
        text.AppendLine(@"       WukongBench ""D:\SteamLibrary\steamapps\common\Black Myth Wukong Benchmark Tool""");
        return text.ToString();
    }
}