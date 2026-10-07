using System.Diagnostics;
using System.Globalization;

namespace WukongBench;

/// <summary>
/// Программа запускает весь сценарий и собирает отчёт:
///
///   1. находит установку Benchmark Tool;
///   2. восстанавливает конфиг, если прошлый запуск упал;
///   3. прогоняет CPU-тест и GPU-тест, каждый со своим профилем настроек;
///   4. сверяет заданные настройки с применёнными игрой;
///   5. печатает отчёт и возвращает конфиг пользователю.
///
/// Запуск:
///   WukongBench                                        путь ищется автоматически
///   WukongBench "D:\...\Black Myth Wukong Benchmark Tool"   путь указан вручную
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Отчёт печатаем в инвариантной культуре: в русской локали получилось бы «16,865 мс»,
        // что легко прочитать как шестнадцать тысяч.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTime.Now;
        SettingsFile? settings = null;

        try
        {
            Log("=== WukongBench ===");
            Log($"Версия: {BuildStamp.Version} ({BuildStamp.Commit})");
            Log($"Запуск {startedAt:yyyy-MM-dd HH:mm:ss}, система: {Environment.OSVersion}");

            var userPath = args.FirstOrDefault(a => !a.StartsWith('-'));

            if (!OperatingSystem.IsWindows())
                return RunOffline(userPath, startedAt, stopwatch);

            var locator = new SteamGameLocator(Log);
            var installDir = locator.FindInstallDir(userPath);
            var exePath = SteamGameLocator.Validate(installDir)
                          ?? throw new FileNotFoundException($"Не найден исполняемый файл в {installDir}");

            settings = new SettingsFile(SteamGameLocator.SettingsPath(installDir), Log);
            settings.RecoverAfterCrash();
            settings.Load();
            settings.Backup();
            Log("Конфиг сохранён в бэкап: после тестов верну ваши настройки как были.");
            Log("");

            var runner = new BenchmarkRunner(SteamGameLocator.ResultsDirectory(), Log);
            var passes = new List<BenchmarkPass>();

            foreach (var profile in BenchmarkProfile.All)
            {
                Log($"========== ПРОХОД {profile.Name} ==========");
                passes.Add(runner.Run(profile, exePath, settings));
                Log("");
            }

            ReportPrinter.Print(SystemInfo.Collect(Log), passes, startedAt, stopwatch.Elapsed);
            return 0;
        }
        catch (PlatformNotSupportedException e)
        {
            Error(e.Message);
            return 2;
        }
        catch (FileNotFoundException e)
        {
            Error(e.Message);
            return 3;
        }
        catch (Exception e)
        {
            Error(e.ToString());
            return 1;
        }
        finally
        {
            // Конфиг возвращаем в любом случае — даже если что-то упало. Пользователь
            // не должен остаться с нашими настройками из-за нашей ошибки.
            if (settings is not null)
            {
                try
                {
                    settings.Restore();
                }
                catch (Exception e)
                {
                    Error($"Не удалось восстановить конфиг: {e.Message}. " +
                          $"Резервная копия осталась здесь: {settings.BackupPath}");
                }
            }

            Log("");
            Log($"Готово за {stopwatch.Elapsed.TotalMinutes:F1} мин.");
        }
    }

    /// <summary>
    /// Без Windows: собираем отчёт по уже сохранённому результату. Полезно, чтобы
    /// посмотреть формат вывода, не имея под рукой бенчмарка.
    /// </summary>
    private static int RunOffline(string? path, DateTime startedAt, Stopwatch stopwatch)
    {
        path ??= Path.Combine(Environment.CurrentDirectory, "samples", "1791219012");

        Log("Бенчмарк запускается только на Windows: прохождение меню сделано через Win32 SendInput.");
        Log("Показан отчёт по сохранённому результату.");
        Log($"");

        var result = BenchmarkResult.TryLoad(path);
        if (result is null)
        {
            Error($"Не удалось прочитать результат: {path}");
            return 3;
        }

        Log($"Прочитан результат: {path}");

        var passes = new List<BenchmarkPass>
        {
            new(BenchmarkProfile.Cpu, path, result),
            new(BenchmarkProfile.Gpu, path, result),
        };

        ReportPrinter.Print(SystemInfo.Collect(Log), passes, startedAt, stopwatch.Elapsed);
        return 0;
    }

    private static void Log(string message) => Console.WriteLine(message);

    private static void Error(string message)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("!!! ОШИБКА !!!");
        Console.Error.WriteLine(message);
    }
}