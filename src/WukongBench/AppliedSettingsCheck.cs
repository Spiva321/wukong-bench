namespace WukongBench;

public sealed class AppliedSettingsCheck
{
    public sealed record Row(string Setting, string Expected, string Actual, bool Ok);

    public static IReadOnlyList<Row> Compare(BenchmarkProfile profile, BenchmarkResult result)
    {
        var quality = profile.QualityLevel.ToString();
        var onOff = profile.RayTracing ? "вкл" : "выкл";

        var rows = new List<Row>
        {
            Check("Масштаб рендеринга", $"{profile.RenderScalePercent}%", $"{result.ImageQuality}%"),

            Check("Ray tracing", onOff, Flag(result.Rtx)),
            Check("Генерация кадров", "выкл", Flag(result.InsertFrame)),
            Check("Motion blur", "0", result.MotionBlur.ToString()),

            Check("Дальность прорисовки", quality, result.ViewDistance.ToString()),
            Check("Сглаживание", quality, result.AntiAliasing.ToString()),
            Check("Постобработка", quality, result.PostProcessing.ToString()),
            Check("Тени", quality, result.ShadowQuality.ToString()),
            Check("Текстуры", quality, result.TextureQuality.ToString()),
            Check("Материалы", quality, result.MaterialQuality.ToString()),
            Check("Растительность", quality, result.VegetationQuality.ToString()),
        };

        // Поля, шкалу которых мы не знаем. Показать надо, оценивать нельзя:
        // мы их не задавали, поэтому «правильного» значения не знаем.
        rows.Add(new Row("Разрешение окна (не задаём)", "—", result.ScreenResolution, true));
        rows.Add(new Row("QualityLevel (не задаём)", "—", result.QualityLevel.ToString(), true));
        rows.Add(new Row("Апскейлер (не задаём)", "—", result.Dlss.ToString(), true));

        return rows;
    }

    public static IReadOnlyList<string> Warnings(BenchmarkProfile profile, BenchmarkResult result)
    {
        var warnings = new List<string>();

        foreach (var row in Compare(profile, result).Where(r => !r.Ok))
            warnings.Add(
                $"Игра применила «{row.Setting}» = {row.Actual}, а инструмент задавал {row.Expected}. " +
                "Замер выполнен не на выбранных настройках, результат нельзя считать корректным.");

        if (result.InsertFrame != 0)
            warnings.Add(
                "Генерация кадров оказалась включена: FPS завышен, а доли CPU- и GPU-кадров " +
                "бессмысленны — при генерации кадров соотношение времён кадра переворачивается.");

        if (profile.RayTracing && result.Rtx == 0)
            warnings.Add(
                "Трассировка лучей не включилась, хотя профиль её требовал: " +
                "GPU-тест прошёл слабее задуманного.");

        if (profile.RenderScalePercent < 100 && result.ImageQuality >= 100)
            warnings.Add(
                "Масштаб рендеринга не снизился: видеокарта отрисовала кадр в полном разрешении, " +
                "CPU-тест могл упереться в GPU.");

        if (result.InsertFrame == 0)
        {
            if (profile.Name == "CPU" && result.CpuBoundShare < 60)
                warnings.Add(
                    $"Ожидали CPU-лимит, но процессор успевал лишь в {result.CpuBoundShare:F1}% кадров. " +
                    "Видеокарта всё ещё была узким местом — стоит снизить масштаб рендеринга.");

            if (profile.Name == "GPU" && result.GpuBoundShare < 60)
                warnings.Add(
                    $"Ожидали GPU-лимит, но видеокарта была узким местом лишь в {result.GpuBoundShare:F1}% кадров. " +
                    "Процессор не успевает — стоит снизить качество или разрешение.");
        }

        warnings.Add(
            "Четыре параметра качества (глобальное освещение, отражения, эффекты, детализация шейдеров) " +
            "бенчмарк в JSON не возвращает — проверить их применение невозможно, доверяем конфигу.");

        return warnings;
    }

    private static Row Check(string name, string expected, string actual) =>
        new(name, expected, actual,
            string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase));

    private static string Flag(int value) => value == 0 ? "выкл" : "вкл";
}