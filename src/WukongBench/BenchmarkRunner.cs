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
    /// Прекратить кликать. К этому моменту тест заканчивается и появляется экран
    /// результатов, где есть кнопка «Пройти заново» — наши клики в неё попадать не должны.
    /// </summary>
    private static readonly TimeSpan StopClickingAfter = TimeSpan.FromSeconds(170);

    /// <summary>
    /// Аварийное закрытие окна. Нужно только если JSON так и не появился: бенчмарк
    /// пишет результат сам, примерно на 206-й секунде, и к этому моменту мы уже вышли.
    /// Если вдруг не вышли — закрываем, чтобы не висеть до таймаута.
    /// </summary>
    private static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(420);

    /// <summary>Сколько ждать JSON после отправки запроса на закрытие окна.</summary>
    private static readonly TimeSpan WaitForResultAfterClose = TimeSpan.FromSeconds(60);

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
        var closeRequestedAt = DateTime.MinValue;
        var lastTitle = "";
        var shotDir = Path.Combine(Path.GetTempPath(), "wukongbench-shots", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        var lastShot = DateTime.MinValue;
        var lastMissingWindowLog = DateTime.MinValue;

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

                // Заголовок нужен только для диагностики: у бенчмарка он всегда «b1»
                // и не меняется при смене экрана, так что опереться на него нельзя.
                var title = NativeInput.GetWindowTitle(window);
                if (title != lastTitle)
                {
                    _log($"  окно: {title} (заголовок не меняется — для диагностики)");
                    lastTitle = title;
                }

                if (NativeInput.IsForeground(window))
                {
                    var elapsed = DateTime.UtcNow - runStart;

                    // Диагностика: раз в 60 секунд снимаем экран и перечисляем папку результатов.
                    if (DateTime.UtcNow - lastShot > TimeSpan.FromSeconds(60))
                    {
                        lastShot = DateTime.UtcNow;
                        var shot = ScreenCapture.Capture(
                            Path.Combine(shotDir, $"{elapsed.TotalSeconds:F0}s.png"));
                        if (shot is not null) _log($"  скриншот: {shot}");
                        _log("  " + DescribeResultsFolder());
                    }

                    // Бенчмарк пишет JSON сам, когда тест дошёл до конца (проверено на прогоне:
                    // файл появился на 206-й секунде без всякого закрытия окна).
                    // Закрываем только аварийно — если результат так и не появился.
                    if (!closeRequested && elapsed > CloseAfter)
                    {
                        _log($"Прошло {elapsed.TotalSeconds:F0} с — прошу окно закрыться штатно, " +
                             "чтобы бенчмарк записал JSON.");
                        NativeInput.RequestClose(window);
                        closeRequested = true;
                        closeRequestedAt = DateTime.UtcNow;
                    }

                    // Кликаем только пока идёт тест. После его окончания на экране есть
                    // кнопка «Пройти заново», и наши клики могли бы запустить тест заново.
                    if (!closeRequested && elapsed < StopClickingAfter)
                    {
                        MenuStep(window, step);
                        step++;
                    }
                }
            }
            else
            {
                // Не засоряем консоль: сообщение раз в 30 секунд, а не на каждой итерации.
                if (DateTime.UtcNow - lastMissingWindowLog > TimeSpan.FromSeconds(30))
                {
                    lastMissingWindowLog = DateTime.UtcNow;
                    _log("  окно бенчмарка не найдено — ждём, пока появится");
                }
            }

            if (NativeInput.IsMouseHeld) userTouchedMouse = true;

            // Мы попросили окно закрыться, а JSON всё не появился — окно, скорее всего,
            // не закрылось. Повторяем запрос: WM_CLOSE игнорируется, если на экране
            // диалог с вопросом.
            if (closeRequested &&
                DateTime.UtcNow - closeRequestedAt > WaitForResultAfterClose &&
                watcher.TryTakeNew(out _, out _) == false &&
                window != IntPtr.Zero)
            {
                _log("JSON не появился — повторно прошу окно закрыться.");
                NativeInput.RequestClose(window);
                closeRequestedAt = DateTime.UtcNow;
            }

            Thread.Sleep(StepDelay);
        }

        menuOk = false;

        _log($"Скриншоты прогона: {shotDir}");

        if (userTouchedMouse)
            _log("ВНИМАНИЕ: во время прогона двигалась мышь — результат может быть недостоверным.");

        ReportResultsFolderOnFailure();
        return null;
    }

    /// <summary>
    /// Перечисляет содержимое папки результатов. Нужно для диагностики: показывает,
    /// появился ли вообще JSON и в каком он состоянии.
    /// </summary>
    private string DescribeResultsFolder()
    {
        if (!Directory.Exists(_resultsDir)) return $"папки результатов нет: {_resultsDir}";

        var files = Directory.EnumerateFiles(_resultsDir).ToList();
        if (files.Count == 0) return $"в папке результатов пока пусто ({_resultsDir})";

        var names = files.Select(Path.GetFileName).Take(3);
        return $"в папке результатов {files.Count} файл(ов): {string.Join(", ", names)}";
    }

    /// <summary>
    /// Диагностика после провала: что реально лежит в папке результатов.
    /// Помогает понять, записал бенчмарк файл или нет.
    /// </summary>
    private void ReportResultsFolderOnFailure()
    {
        _log("");
        _log($"Итог: {DescribeResultsFolder()}");
        _log($"Ожидаемое место: {_resultsDir}");
        _log("Бенчмарк пишет JSON при выходе из программы. Если файл появился, но не прочитался —");
        _log("значит он был дописан не полностью.");
    }

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