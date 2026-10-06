using System.Text;

namespace WukongBench;

public sealed record BenchmarkPass(
    BenchmarkProfile Profile,
    string RawResultPath,
    BenchmarkResult Result);

public static class ReportPrinter
{
    private const int Width = 78;

    public static void Print(
        SystemInfo system,
        IReadOnlyList<BenchmarkPass> passes,
        DateTime startedAt,
        TimeSpan elapsed)
    {
        var text = new StringBuilder();

        Header(text, startedAt);
        Machine(text, system);
        Comparison(text, passes);
        PerPass(text, passes);
        Applied(text, passes);
        SelfCheck(text, passes);
        Footer(text, elapsed);

        var report = text.ToString();
        Console.WriteLine(report);
        Save(report, passes, startedAt);
    }

    private static void Header(StringBuilder text, DateTime startedAt)
    {
        Rule(text, '=');
        Center(text, "BLACK MYTH: WUKONG — АВТОМАТИЧЕСКИЙ БЕНЧМАРК");
        Rule(text, '=');
        text.AppendLine($"  Запуск:      {startedAt:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"  Инструмент:  WukongBench (.NET 8)");
        text.AppendLine();
    }

    private static void Machine(StringBuilder text, SystemInfo system)
    {
        Section(text, "ХАРАКТЕРИСТИКИ КОМПЬЮТЕРА");

        if (!system.IsWindows)
        {
            text.AppendLine("  WMI недоступен: программа запущена не на Windows.");
            text.AppendLine("  Характеристики ПК не собраны.");
            text.AppendLine();
            return;
        }

        Row(text, "Процессор", system.CpuName);

        var cores = system.CpuCores > 0
            ? $"{system.CpuCores} / {system.CpuLogicalProcessors}"
              + (system.CpuMaxClockMhz > 0 ? $", до {system.CpuMaxClockMhz} МГц" : "")
            : "не определено";
        Row(text, "Ядра / потоки", cores);

        Row(text, "Видеокарта", system.GpuName);
        Row(text, "Драйвер видеокарты", system.GpuDriverVersion);
        Row(text, "Память видеокарты", system.GpuRam);

        Row(text, "Оперативная память",
            system.TotalRamMb > 0 ? $"{system.TotalRamMb / 1024.0:F1} ГБ" : "не определено");
        Row(text, "Модули памяти", system.RamModules);

        Row(text, "Материнская плата", system.Mainboard);
        Row(text, "Диск", $"{system.DiskModel} ({system.DiskType})");
        Row(text, "Операционная система", $"{system.OsName} {system.OsVersion}");
        text.AppendLine();
    }

    private static void Comparison(StringBuilder text, IReadOnlyList<BenchmarkPass> passes)
    {
        Section(text, "РЕЗУЛЬТАТЫ ДВУХ ПРОХОДОВ");

        if (passes.Count == 0)
        {
            text.AppendLine("  Прогонов не было.");
            text.AppendLine();
            return;
        }

        var colWidth = 18;
        var labelWidth = passes.Count * colWidth + 2;

        text.Append("  ");
        text.Append(Pad("Метрика", labelWidth - 2));
        foreach (var pass in passes) text.Append(PadRight(pass.Profile.Name + "-тест", colWidth));
        text.AppendLine();
        text.AppendLine(new string('-', Width));

        foreach (var (label, get) in Metrics)
        {
            text.Append($"  {Pad(label, labelWidth)}");
            foreach (var pass in passes) text.Append(PadRight(get(pass.Result), colWidth));
            text.AppendLine();
        }

        text.AppendLine();
    }

    private static readonly (string Label, Func<BenchmarkResult, string> Get)[] Metrics =
    [
        ("FPS средний", r => Num(r.FPSAvg, 1)),
        ("FPS минимальный", r => Num(r.FPSMin, 1)),
        ("FPS 1% low (5-й процентиль)", r => Num(r.FPS95, 1)),
        ("FPS максимальный", r => Num(r.FPSMax, 1)),
        ("Кадр CPU, мс", r => Num(r.AvgCpuFrameTime, 2)),
        ("Кадр GPU, мс", r => Num(r.AvgGpuFrameTime, 2)),
        ("Загрузка GPU, %", r => Num(r.GPUAvg, 1)),
        ("Кадров CPU-лимитом, %", r => Num(r.CpuBoundShare, 1)),
        ("Кадров GPU-лимитом, %", r => Num(r.GpuBoundShare, 1)),
        ("Видеопамять пик, ГБ", r => Num(r.PeakVideoMemory, 2)),
        ("Длительность, с", r => Num(r.DurationSeconds, 1)),
        ("Кадров в прогоне", r => r.Records.Count.ToString()),
    ];

    private static void PerPass(StringBuilder text, IReadOnlyList<BenchmarkPass> passes)
    {
        Section(text, "ПОДРОБНОСТИ ПО ПРОХОДАМ");

        foreach (var pass in passes)
        {
            var r = pass.Result;
            text.AppendLine($"  ПРОХОД {pass.Profile.Name}");
            Rule(text, '-');
            Row2(text, "Окно", r.ScreenResolution);
            Row2(text, "Масштаб рендеринга", $"{r.ImageQuality}%");
            Row2(text, "Качество", r.QualityLevel.ToString());
            Row2(text, "Трассировка лучей", Flag(r.Rtx));
            Row2(text, "Генерация кадров", Flag(r.InsertFrame));
            Row2(text, "Размытие в движении", r.MotionBlur.ToString());
            Row2(text, "Апскейлер", $"{r.Dlss} (инструментом не задавался)");
            Row2(text, "Версия игры", r.GameVer);
            text.AppendLine();

            text.AppendLine("    Данные о машине по отчёту самого бенчмарка:");
            Row2(text, "CPU", r.CPUModel);
            Row2(text, "GPU", $"{r.GPUModel} {r.VideoMemSize}");
            Row2(text, "Драйвер", r.GpuDriverVer);
            Row2(text, "RAM", r.SysMem);
            Row2(text, "Система", r.SysVer);
            text.AppendLine();
        }
    }

    private static void Applied(StringBuilder text, IReadOnlyList<BenchmarkPass> passes)
    {
        Section(text, "НАСТРОЙКИ, ИСПОЛЬЗОВАННЫЕ В КАЖДОМ ПРОХОДЕ");

        foreach (var pass in passes)
        {
            text.AppendLine($"  Профиль {pass.Profile.Name}");
            Rule(text, '-');
            Row2(text, "Масштаб рендеринга", $"{pass.Profile.RenderScalePercent}%");
            Row2(text, "Качество (10 параметров)", $"{pass.Profile.QualityLevel} из 5");
            Row2(text, "Трассировка лучей", pass.Profile.RayTracing ? "вкл" : "выкл");
            Row2(text, "Vsync / лимит FPS", "выкл");
            Row2(text, "Генерация кадров", "выкл");
            Row2(text, "Размытие в движении", "выкл");
            text.AppendLine();

            text.AppendLine("    Не задавалось инструментом (допустимые значения неизвестны):");
            text.AppendLine("      разрешение окна — индекс в списке, оставлено как было");
            text.AppendLine("      апскейлер SuperResolutionSampling — оставлен как был");
            text.AppendLine("      QualityLevel — шкала неизвестна, оставлен как был");
            text.AppendLine();

            text.AppendLine("    Почему выбраны такие настройки:");
            foreach (var line in Wrap(pass.Profile.Rationale, 66))
                text.AppendLine($"      {line}");
            text.AppendLine();
        }
    }

    private static void SelfCheck(StringBuilder text, IReadOnlyList<BenchmarkPass> passes)
    {
        Section(text, "ПРОВЕРКА: ЧТО ЗАДАЛ ИНСТРУМЕНТ, А ЧТО ПРИМЕНИЛА ИГРА");

        foreach (var pass in passes)
        {
            text.AppendLine($"  Профиль {pass.Profile.Name}");
            Rule(text, '-');

            foreach (var row in AppliedSettingsCheck.Compare(pass.Profile, pass.Result))
                text.AppendLine($"    [{(row.Ok ? "OK  " : "РАЗМ")}] {row.Setting,-30} " +
                                $"ждали {row.Expected,-6} получили {row.Actual}");

            text.AppendLine();
        }

        var warnings = passes
            .SelectMany(p => AppliedSettingsCheck.Warnings(p.Profile, p.Result)
                             .Select(w => $"[{p.Profile.Name}] {w}"))
            .ToList();

        Section(text, "ПРЕДУПРЕЖДЕНИЯ");

        if (warnings.Count == 0)
        {
            text.AppendLine("  нет");
            text.AppendLine();
            return;
        }

        foreach (var warning in warnings) text.AppendLine($"  {warning}");
        text.AppendLine();
    }

    private static void Footer(StringBuilder text, TimeSpan elapsed)
    {
        Rule(text, '=');
        text.AppendLine($"  Всего: {elapsed.TotalMinutes:F1} мин");
        text.AppendLine();
        text.AppendLine("  Примечания:");
        text.AppendLine("   • FPS95 в файле бенчмарка — это 5-й процентиль, по смыслу «1% low».");
        text.AppendLine("   • CPUUsage в бенчмарке всегда равен 1 — заглушка; загрузка ЦП");
        text.AppendLine("     считается по времени кадра, а не по нему.");
        Rule(text, '=');
    }

    private static void Save(string report, IReadOnlyList<BenchmarkPass> passes, DateTime startedAt)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "WukongBench", "results", startedAt.ToString("yyyy-MM-dd_HH-mm-ss"));

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.txt"), report);

            foreach (var pass in passes)
            {
                if (!string.IsNullOrEmpty(pass.RawResultPath) && File.Exists(pass.RawResultPath))
                    File.Copy(pass.RawResultPath,
                              Path.Combine(dir, $"{pass.Profile.Name.ToLowerInvariant()}_raw.json"),
                              overwrite: true);
            }

            Console.WriteLine();
            Console.WriteLine($"Отчёт сохранён: {dir}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Не удалось сохранить отчёт: {e.Message}");
        }
    }

    private static string Num(double value, int decimals) =>
        value.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture);

    private static string Flag(int value) => value == 0 ? "выкл" : "вкл";

    private static string Pad(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadRight(width);

    private static string PadRight(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadLeft(width);

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine();
        Rule(text, '-');
        text.AppendLine($" {title}");
        Rule(text, '-');
    }

    private static void Rule(StringBuilder text, char c) => text.AppendLine(new string(c, Width));

    private static void Center(StringBuilder text, string title) =>
        text.AppendLine(new string(' ', Math.Max(0, (Width - title.Length) / 2)) + title);

    private static void Row(StringBuilder text, string label, string value) =>
        text.AppendLine($"  {label,-22}{value}");

    private static void Row2(StringBuilder text, string label, string value) =>
        text.AppendLine($"    {label,-28}{value}");

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }
}