namespace WukongBench;

/// <summary>
/// Следит за папкой, куда бенчмарк кладёт JSON с результатами, и отдаёт результат
/// именно текущего прогона.
///
/// Три проблемы, которые здесь решаются:
///
/// 1. В папке копится история прошлых прогонов. Если просто ждать «первый файл»,
///    инструмент возьмёт вчерашний результат. Поэтому перед запуском запоминаем,
///    какие файлы уже были, и ждём только новые.
///
/// 2. Файл появляется раньше, чем дописывается. Первое чтение может дать обрывок
///    JSON — это не ошибка, а «ещё рано». Поэтому повторяем попытки.
///
/// 3. Бенчмарк может не ответить вовсе. Ждать вечно нельзя — нужен таймаут.
/// </summary>
public sealed class ResultWatcher : IDisposable
{
    private readonly string _directory;
    private readonly Action<string> _log;

    /// <summary>Результат, который уже отдан вызывающему, — чтобы не отдать дважды.</summary>
    private string? _taken;

    /// <summary>Файлы, которые уже были в папке на момент снимка.</summary>
    private readonly HashSet<string> _knownBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Время изменения самого свежего файла на момент снимка. Файл считается новым,
    /// если он свежее. Так не нужно хранить коллекцию имён и не зависит от того,
    /// как бенчмарк называет файлы.
    /// </summary>
    private DateTime _newestKnownWriteTimeUtc = DateTime.MinValue;

    private bool _disposed;

    public ResultWatcher(string directory, Action<string>? log = null)
    {
        _directory = directory;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Запоминает текущее содержимое папки. Вызывать ДО запуска бенчмарка —
    /// иначе весь смысл снимка теряется.
    /// </summary>
    public void NoteExisting()
    {
        _knownBefore.Clear();
        _newestKnownWriteTimeUtc = DateTime.MinValue;

        if (!Directory.Exists(_directory))
        {
            _log($"Папки результатов пока нет: {_directory}");
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            _knownBefore.Add(Path.GetFileName(path));

            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc > _newestKnownWriteTimeUtc)
                _newestKnownWriteTimeUtc = info.LastWriteTimeUtc;
        }

        _log($"В папке результатов уже было файлов: {_knownBefore.Count}");
    }

    /// <summary>
    /// Проверяет папку ровно один раз, ничего не ожидая и ничего не логируя.
    /// Нужен в цикле ожидания: вызывать WaitForResult там нельзя — тот при каждой
    /// неудаче пишет в лог «не появился за таймаут», и консоль засоряется тысячами строк.
    /// </summary>
    public bool TryTakeNew(out string? path, out BenchmarkResult? result)
    {
        path = null;
        result = null;

        var candidate = FindNewestNewFile();
        if (candidate is null || candidate == _taken) return false;

        var parsed = BenchmarkResult.TryLoad(candidate);

        // HasRecords защищает от случая «файл есть, но внутри пусто или обрывок».
        if (parsed is null || !parsed.HasRecords) return false;

        _taken = candidate;
        path = candidate;
        result = parsed;
        return true;
    }

    /// <summary>
    /// Ждёт результата текущего прогона и читает его.
    /// Возвращает null, если за отведённое время ничего не появилось.
    /// </summary>
    public (string Path, BenchmarkResult Result)? WaitForResult(TimeSpan timeout)
    {
        Directory.CreateDirectory(_directory);

        var file = WaitForNewFile(timeout);
        if (file is null)
        {
            _log($"Результат не появился за {timeout.TotalMinutes:F1} мин.");
            return null;
        }

        _log($"Обнаружен новый файл: {Path.GetFileName(file)}. Жду, пока бенчмарк допишет его.");

        // На чтение может понадобиться несколько секунд: файл создан, но запись не закончена.
        var readDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < readDeadline)
        {
            var result = BenchmarkResult.TryLoad(file);

            if (result is not null && result.HasRecords)
            {
                _taken = file;
                _log($"Результат прочитан: кадров {result.Records.Count}, " +
                     $"FPS {result.FPSAvg:F1}, длительность {result.DurationSeconds:F1} с");
                return (file, result);
            }

            Thread.Sleep(400);
        }

        _log("Файл результата появился, но прочитать его так и не удалось.");
        return null;
    }

    /// <summary>Ищет файл, которого не было на момент снимка. null по таймауту.</summary>
    private string? WaitForNewFile(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var candidate = FindNewestNewFile();
            if (candidate is not null) return candidate;

            // Пауза обязательна: без неё цикл загрузит ядро на 100% на все минуты ожидания.
            Thread.Sleep(400);
        }

        return null;
    }

    /// <summary>Самый свежий файл, которого не было на момент снимка.</summary>
    private string? FindNewestNewFile()
    {
        if (!Directory.Exists(_directory)) return null;

        FileInfo? newest = null;

        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var name = Path.GetFileName(path);

            // Отсекаем по имени — это основная проверка.
            if (_knownBefore.Contains(name)) continue;

            var info = new FileInfo(path);

            // И отсекаем по времени — страховка на случай одинаковых имён.
            if (info.LastWriteTimeUtc <= _newestKnownWriteTimeUtc) continue;

            if (newest is null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                newest = info;
        }

        return newest?.FullName;
    }

    /// <summary>Все результаты в папке, новые и старые — для отчёта и отладки.</summary>
    public IReadOnlyList<string> ListAll() =>
        Directory.Exists(_directory)
            ? Directory.EnumerateFiles(_directory, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList()
            : [];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}