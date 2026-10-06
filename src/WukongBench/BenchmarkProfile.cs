namespace WukongBench;

public sealed class BenchmarkProfile
{
    public required string Name { get; init; }
    public required int RenderScalePercent { get; init; }
    public required int QualityLevel { get; init; }
    public required bool RayTracing { get; init; }
    public required string Rationale { get; init; }

        public static readonly string[] QualityUiKeys =
    [
        "ViewDistance", "AntiAliasing", "PostProcessing", "ShadowQuality", "TextureQuality",
        "FxQuality", "MaterialQuality", "VegetationQuality", "GlobalIllumination", "ReflectionQuality",
    ];


    public static readonly string[] QualitySgKeys =
    [
        "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.PostProcessQuality",
        "sg.ShadowQuality", "sg.TextureQuality", "sg.EffectsQuality",
        "sg.FoliageQuality", "sg.GlobalIlluminationQuality", "sg.ReflectionQuality",
        "sg.ShadingQuality",
    ];

     public static BenchmarkProfile Cpu { get; } = new()
    {
        Name = "CPU",
        RenderScalePercent = 50,
        QualityLevel = 1,
        RayTracing = false,

        Rationale =
            "Задача — снять с видеокарты максимум работы, чтобы узким местом остался процессор. " +
            "Масштаб рендеринга снижен до 50%: при окне 1920×1080 игра рисует 960×540 вместо " +
            "1920×1080, то есть вчетверо меньше пикселей. Все десять параметров качества " +
            "(тени, глобальное освещение, отражения, постобработка, дальность прорисовки, " +
            "растительность) поставлены на минимум, что дополнительно разгружает видеокарту. " +
            "Трассировка лучей выключена — это самый дорогой для видеокарты эффект. " +
            "Генерация кадров, VSync и ограничение FPS выключены: генерация кадров подставляет " +
            "искусственные кадры и завышает результат, а VSync ограничивает его сверху. " +
            "Масштаб 50% выбран потому, что ниже Unreal обычно всё равно ограничивает масштаб, " +
            "и запрос меньшего значения не дал бы гарантии. В таких условиях видеокарта успевает " +
            "отрисовать кадр быстрее, чем процессор его посчитать, поэтому FPS отражает " +
            "производительность именно процессора.",
    };

    public static BenchmarkProfile Gpu { get; } = new()
    {
        Name = "GPU",
        RenderScalePercent = 100,
        QualityLevel = 5,
        RayTracing = true,

        Rationale =
            "Задача — нагрузить видеокарту максимально, чтобы она стала узким местом. " +
            "Масштаб рендеринга поставлен в 100%: игра рисует в полном разрешении окна, " +
            "не экономя ни одного пикселя. Все десять параметров качества — на максимум, " +
            "то есть каждый пиксель обрабатывается самыми дорогими шейдерами. Включена " +
            "трассировка лучей — самый дорогой для видеокарты эффект в игре. " +
            "Vsync и ограничение FPS выключены, иначе результат упирался бы в частоту монитора, " +
            "а не в производительность видеокарты. Генерация кадров тоже выключена: она " +
            "искусственно завышает FPS, из-за чего число перестаёт быть показателем " +
            "производительности. Оговорка: трассировка лучей зависит от поколения видеокарты " +
            "сильнее прочих настроек, поэтому для честного сравнения карт разных поколений " +
            "нужен отдельный прогон без неё.",
    };

    public static BenchmarkProfile[] All => [Cpu, Gpu];

    public void ApplyTo(SettingsFile settings)
    {
        var windowHeight = int.Parse(settings.GetIni(SettingsFile.MainSection, "ResolutionSizeY"));
        var renderHeight = (int)Math.Round(windowHeight * RenderScalePercent / 100.0);
        settings.SetUi("ImageQuality", renderHeight.ToString());

        foreach (var key in QualityUiKeys)
            settings.SetUi(key, QualityLevel.ToString());

        var sgLevel = (QualityLevel - 1).ToString();
        var sgValues = QualitySgKeys.ToDictionary(k => k, _ => sgLevel);
        sgValues["sg.ResolutionQuality"] = RenderScalePercent.ToString();
        settings.SetIniBatch(SettingsFile.ScalabilitySection, sgValues);

        settings.SetUi("Rtx", RayTracing ? "1" : "0");

        settings.SetUi("Vsync", "0");
        settings.SetUi("LockFrameRate", "0");
        settings.SetUi("InsertFrame", "0");
        settings.SetUi("MotionBlur", "0");
        settings.SetIni(SettingsFile.MainSection, "bUseVSync", "False");
        settings.SetIni(SettingsFile.MainSection, "FrameRateLimit", "0.000000");

        settings.Save();
    }
}