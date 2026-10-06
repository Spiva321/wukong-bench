using System.Management;

namespace WukongBench;

/// <summary>
/// Собирает характеристики компьютера через WMI.
///
/// Про объём видеопамяти отдельно: свойство Win32_VideoController.AdapterRAM — 32-битное,
/// поэтому у видеокарты с 8 ГБ оно покажет 4 ГБ. Поэтому в отчёт выводится и это значение
/// (с честной пометкой о занижении), и точное из отчёта бенчмарка, которое видно рядом.
///
/// При недоступном WMI метод не бросает исключение, а возвращает объект с пустыми
/// значениями: потеря характеристик ПК не должна ронять весь прогон.
/// </summary>
public sealed class SystemInfo
{
    public string CpuName { get; private set; } = "не определено";
    public int CpuCores { get; private set; }
    public int CpuLogicalProcessors { get; private set; }
    public int CpuMaxClockMhz { get; private set; }

    public string GpuName { get; private set; } = "не определено";
    public string GpuDriverVersion { get; private set; } = "не определено";
    public string GpuRam { get; private set; } = "не определено";

    public long TotalRamMb { get; private set; }
    public string RamModules { get; private set; } = "не определено";

    public string Mainboard { get; private set; } = "не определено";
    public string DiskModel { get; private set; } = "не определено";
    public string DiskType { get; private set; } = "";

    public string OsName { get; private set; } = "не определено";
    public string OsVersion { get; private set; } = "";

    public bool IsWindows { get; private set; } = OperatingSystem.IsWindows();

    public static SystemInfo Collect(Action<string>? log = null)
    {
        var info = new SystemInfo();

        if (!info.IsWindows)
        {
            log?.Invoke("Система не Windows: WMI недоступен, характеристики ПК не собраны.");
            return info;
        }

        info.ReadProcessor();
        info.ReadGraphics();
        info.ReadMemory();
        info.ReadSystem();
        return info;
    }

    private void ReadProcessor()
    {
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed " +
                                  "FROM Win32_Processor"))
        {
            CpuName = Value(cpu, "Name");
            CpuCores = Int(cpu, "NumberOfCores");
            CpuLogicalProcessors = Int(cpu, "NumberOfLogicalProcessors");
            CpuMaxClockMhz = Int(cpu, "MaxClockSpeed");
        }
    }

    private void ReadGraphics()
    {
        foreach (var gpu in Query("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
        {
            var name = Value(gpu, "Name");

            // Виртуальные адаптеры есть у любой машины с удалённым рабочим столом — пропускаем.
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)) continue;

            GpuName = name;
            GpuDriverVersion = Value(gpu, "DriverVersion");

            var bytes = Long(gpu, "AdapterRAM");
            GpuRam = bytes <= 0
                ? "не определено"
                : $"{bytes / 1024 / 1024 / 1024} ГБ (поле ограничено 4 ГБ, значение может быть занижено)";
        }
    }

    private void ReadMemory()
    {
        foreach (var cs in Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            TotalRamMb = Long(cs, "TotalPhysicalMemory") / 1024 / 1024;

        var sticks = Query("SELECT Manufacturer, Capacity, Speed FROM Win32_PhysicalMemory").ToList();
        if (sticks.Count == 0) return;

        var vendor = sticks
            .Select(s => Value(s, "Manufacturer"))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var vendorText = vendor.Any() ? string.Join(" + ", vendor) : "неизвестно";
        var speed = sticks.Select(s => Int(s, "Speed")).FirstOrDefault(s => s > 0);
        var perStickGb = Long(sticks[0], "Capacity") / 1024 / 1024 / 1024;

        RamModules = $"{vendorText}, {sticks.Count} × {perStickGb} ГБ";
        if (speed > 0) RamModules += $", {speed} МГц";
    }

    private void ReadSystem()
    {
        foreach (var os in Query("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem"))
        {
            OsName = Value(os, "Caption");
            OsVersion = $"{Value(os, "Version")} (build {Value(os, "BuildNumber")})";
        }

        foreach (var board in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            Mainboard = $"{Value(board, "Manufacturer")} {Value(board, "Product")}".Trim();
        }

        // Из дисков берём самый большой — обычно это системный, он точно влияет на результат.
        long bestSize = -1;
        foreach (var disk in Query("SELECT Model, MediaType, Size FROM Win32_DiskDrive"))
        {
            if (Long(disk, "Size") <= bestSize) continue;

            bestSize = Long(disk, "Size");
            DiskModel = Value(disk, "Model");

            var media = Value(disk, "MediaType");
            DiskType = media.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe SSD"
                : media.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                : media.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD"
                : media.Length > 0 ? media
                : "не определён";
        }
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        ManagementObjectCollection? results = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            results = searcher.Get();
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException)
        {
            yield break;
        }

        if (results is null) yield break;

        using (results)
        {
            foreach (ManagementBaseObject item in results) yield return item;
        }
    }

    private static string Value(ManagementBaseObject obj, string property) =>
        obj[property]?.ToString()?.Trim() ?? "";

    private static int Int(ManagementBaseObject obj, string property) =>
        int.TryParse(Value(obj, property), out var v) ? v : 0;

    private static long Long(ManagementBaseObject obj, string property) =>
        long.TryParse(Value(obj, property), out var v) ? v : 0;
}