using System.Diagnostics;

namespace WukongBench;

/// <summary>
/// Один полный проход: применить профиль настроек, запустить бенчмарк, пройти меню,
/// дождаться результата, принудительно закрыть процесс.
///
/// Три решения, на которых держится вся надёжность:
///
/// 1. Результат проверяется ДО любого клика. На экране итогов есть кнопка «Пройти заново» —
///    клик по ней запустил бы лишний прогон, и следующий тест пошёл бы не по тем настройкам.
///
/// 2. Процесс убивается, а не закрывается штатно. При нормальном выходе игра сохраняет
///    свои настройки и перезаписывает наш конфиг — тогда второй проход пойдёт на
///    настройках первого. Kill не даёт дописать файл.
///
/// 3. Кликаем только когда наше окно реально на переднем плане. Иначе клик ушёл бы
///    в чужое приложение.
/// </summary>
public sealed class BenchmarkRunner
{
    // Доли клиентской области окна. ⚠️ Если кнопки не нажимаются — правьте здесь.
    private const double StartButtonX = 0.10, StartButtonY = 0.45;      // «Тест быстродействия»
    private const double ConfirmButtonX = 0.39, ConfirmButtonY = 0.59;    // «Подтвердить»

    private static readonly TimeSpan StepDelay = TimeSpan.FromSeconds(3);

    /// <summary>Два прохода по 3–5 минут плюс загрузка — 20 минут с запасом.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Минимальное время до попытки закрыть окно. Тест идёт около 140 секунд по данным
    /// кадров, плюс загрузка сцен и меню — раньше закрывать нельзя, иначе тест оборвётся.
    /// </summary>
    private static readonly TimeSpan MinimumRunDuration = TimeSpan.FromSeconds(150);

    private readonly string _resultsDir;
    private readonly Action<string> _log;

    public BenchmarkRunner(string resultsDir, Action<string>? log = null)
    {
        _resultsDir = resultsDir;
        _log = log ?? (_ => { });
    }

    public BenchmarkPass Run(BenchmarkProfile profile, string exePath, SettingsFile settings)
    {
        if (!NativeInput.IsSupported)
            throw new PlatformNotSupportedException(
                "Бенчмарк запускается только на Windows: прохождение меню сделано через Win32 SendInput, " +
                "в других системах это не работает.");

        EnsureNotRunning();

        // Снимок ДО запуска — иначе примем вчерашний результат за текущий.
        using var watcher = new ResultWatcher(_resultsDir, _log);
        watcher.NoteExisting();

        settings.Load();
        profile.ApplyTo(settings);
        _log($"Профиль {profile.Name} записан в конфиг.");

        StartProcess(exePath);
        _log("Бенчмарк запущен. Не трогайте мышь и клавиатуру — инструмент сам проходит меню.");

        var result = WaitPassingMenu(watcher, out var menuOk);

        // Убиваем всегда, даже если результат не дождались: иначе процесс останется висеть.
        KillBenchmark();

        if (result is null)
            throw new TimeoutException(
                $"Бенчмарк не выдал результат за {Timeout.TotalMinutes:F0} мин. " +
                "Проверьте, что Steam запущен и что бенчмарк установлен.");

        if (!menuOk)
            _log("ВНИМАНИЕ: меню пройдено не полностью — возможно, координаты кнопок нужно поправить.");

        _log($"Проход завершён: {result.Value.Result.FPSAvg:F1} FPS");
        return new BenchmarkPass(profile, result.Value.Path, result.Value.Result);
    }

    // ---------------- запуск и закрытие ----------------

    /// <summary>
    /// Steam должен быть запущен: у бенчмарка есть проверка прав через Steam API,
    /// без неё он завершается с «Failed entitlement check with Steam API».
    /// </summary>
    private static void StartProcess(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? exePath,
        })?.Dispose();

    private static IEnumerable<Process> FindBenchmarkProcesses() =>
        SteamGameLocator.ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .Where(p =>
            {
                try { return !p.HasExited; }
                catch (InvalidOperationException) { return false; }
            });

    private void EnsureNotRunning()
    {
        if (FindBenchmarkProcesses().Any())
            throw new InvalidOperationException(
                "Бенчмарк уже запущен. Закройте его и повторите — иначе настройки двух прогонов смешаются.");
    }

    private void KillBenchmark()
    {
        foreach (var process in FindBenchmarkProcesses())
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
                _log("Процесс бенчмарка закрыт.");
            }
            catch (InvalidOperationException)
            {
                _log("Процесс бенчмарка завершился сам.");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or NotSupportedException)
            {
                _log($"Не удалось закрыть процесс: {e.Message}");
            }
        }
    }

    // ---------------- прохождение меню ----------------

    private (string Path, BenchmarkResult Result)? WaitPassingMenu(ResultWatcher watcher, out bool menuOk)
    {
        var runStart = DateTime.UtcNow;
        var deadline = runStart + Timeout;
        var step = 0;
        var userTouchedMouse = false;
        var closeRequested = false;
        var lastTitle = "";
        var shotDir = Path.Combine(Path.GetTempPath(), "wukongbench-shots", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        var lastShot = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            // JSON может появиться и раньше, если игра пишет его сразу. Проверяем молча,
            // без логирования: иначе в консоль посыплется тысяча одинаковых строк.
            var ready = watcher.TryTakeNew(out var file, out var result);
            if (ready)
            {
                menuOk = true;
                _log($"Прочитан результат: {file}");
                return (file!, result!);
            }

            var window = FindGameWindow();
            if (window != IntPtr.Zero)
            {
                if (!NativeInput.IsForeground(window)) NativeInput.TryActivate(window);

                // Заголовок окна меняется при смене экрана — это наш главный признак,
                // что бенчмарк продвинулся дальше, а не застрял.
                var title = NativeInput.GetWindowTitle(window);
                if (title != lastTitle)
                {
                    _log($"  окно: {title}");
                    lastTitle = title;
                }

                if (NativeInput.IsForeground(window))
                {
                    var elapsed = DateTime.UtcNow - runStart;

                    // Диагностика: снимаем экран раз в 20 секунд, чтобы видеть, где застряли.
                    if (DateTime.UtcNow - lastShot > TimeSpan.FromSeconds(20))
                    {
                        lastShot = DateTime.UtcNow;
                        var shot = ScreenCapture.Capture(
                            Path.Combine(shotDir, $"{elapsed.TotalSeconds:F0}s.png"));
                        if (shot is not null) _log($"  скриншот: {shot}");
                    }

                    // Закрываем окно только когда оно явно показывает экран результатов.
                    // Прежде мы закрывали вслепую по таймеру и обрывали тест на середине.
                    if (!closeRequested && IsResultsScreen(title, elapsed))
                    {
                        _log($"Прошло {elapsed.TotalSeconds:F0} с, окно «{title}» — похоже на результаты, " +
                             "прошу закрыть штатно, чтобы бенчмарк записал JSON.");
                        NativeInput.RequestClose(window);
                        closeRequested = true;
                    }

                    // После запроса на закрытие кликать нельзя: если это экран результатов,
                    // есть кнопка «Пройти заново».
                    if (!closeRequested)
                    {
                        MenuStep(window, step);
                        step++;
                    }
                }
            }
            else
            {
                _log("  окно бенчмарка не найдено — ждём, пока появится");
            }

            if (NativeInput.IsMouseHeld) userTouchedMouse = true;

            Thread.Sleep(StepDelay);
        }

        menuOk = false;

        _log($"Скриншоты прогона: {shotDir}");

        if (userTouchedMouse)
            _log("ВНИМАНИЕ: во время прогона двигалась мышь — результат может быть недостоверным.");

        return null;
    }

    /// <summary>
    /// Похоже ли окно на экран результатов.
    ///
    /// Два признака: заголовок содержит «Benchmark» или «Result», и прошло достаточно
    /// времени. Второе условие важно: сразу после запуска заголовок такой же, а тест
    /// ещё не начинался.
    /// </summary>
    private static bool IsResultsScreen(string title, TimeSpan elapsed) =>
        elapsed > MinimumRunDuration &&
        (title.Contains("Result", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("Benchmark", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Шаги повторяются по кругу: пока тест не пошёл, лишние клики попадают в безобидные
    /// места, а нужная кнопка в итоге нажимается.
    /// </summary>
    private static void MenuStep(IntPtr window, int step)
    {
        switch (step % 3)
        {
            case 0:
                NativeInput.PressKey(NativeInput.VkReturn); // заставка убирается любой клавишей
                break;

            case 1:
                NativeInput.ClickRelative(window, StartButtonX, StartButtonY);
                break;

            case 2:
                NativeInput.ClickRelative(window, ConfirmButtonX, ConfirmButtonY);
                break;
        }
    }

    private static IntPtr FindGameWindow()
    {
        try
        {
            return FindBenchmarkProcesses()
                .Select(p => p.MainWindowHandle)
                .FirstOrDefault(h => h != IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }
}