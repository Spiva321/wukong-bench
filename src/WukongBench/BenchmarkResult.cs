using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WukongBench;

public sealed class FrameRecord
{
    public long TimeStamp {get; set;}
    public double FrameRate {get; set;}
    public double CPUUsage {get; set;}
    public double GPUUsage {get; set;}
    public double CPUFrameTime {get; set;}
    public double GPUFrameTime {get; set;}
    public double VideoMemoryUsage {get; set;}

}

public sealed class BenchmarkResult
{    
    public long TimeStamp {get; set;}
    public int BenckMarkVersion {get; set;}
    public double FPSAvg {get; set;}
    public double FPSMax {get; set;}
    public double FPSMin {get; set;}
    public double FPS95 {get; set;}
    public double CPUAvg {get; set;}
    public double GPUAvg {get; set;}
    public double GPULeak {get; set;}
    public double VideoMem {get; set;}

    public string GameVer { get; set; } = "";
    public string SysVer { get; set; } = "";
    public string CPUModel { get; set; } = "";
    public string GPUModel { get; set; } = "";
    public string GpuDriverVer { get; set; } = "";
    public string VideoMemSize { get; set; } = "";
    public string SysMem { get; set; } = "";


    public int ScreenMode { get; set; }
    public string ScreenResolution { get; set; } = "";
    public int QualityLevel { get; set; }
    public int ImageQuality { get; set; }
    public int ViewDistance { get; set; }
    public int AntiAliasing { get; set; }
    public int PostProcessing { get; set; }
    public int ShadowQuality { get; set; }
    public int TextureQuality { get; set; }
    public int MaterialQuality { get; set; }
    public int VegetationQuality { get; set; }
    public int MotionBlur { get; set; }
    public int Rtx { get; set; }
    public int Dlss { get; set; }
    public int InsertFrame { get; set; }
    public int Dx12 { get; set; }

    public List<FrameRecord> Records { get; set; } = [];

    [JsonIgnore] public bool HasRecords => Records.Count > 0;

    [JsonIgnore]
    public double DurationSeconds
    {
        get
        {
            if(Records.Count < 2) return 0;
            return (Records[^1].TimeStamp - Records[0].TimeStamp) / 1000.0;
        }
    }

    [JsonIgnore]
    public double AvgFrameRate
    {
        get
        {
            if (!HasRecords) return 0;
            return Records.Average(r => r.FrameRate);
        }
    }

    [JsonIgnore]
    public double AvgCpuFrameTime
    {
        get
        {
            if (!HasRecords) return 0;
            return Records.Average(r => r.CPUFrameTime);
        }
    }

    [JsonIgnore]
    public double AvgGpuFrameTime
    {
        get
        {
            if (!HasRecords) return 0;
            return Records.Average(r => r.GPUFrameTime);
        }
    }

    [JsonIgnore]
    public double PeakVideoMemory
    {
        get
        {
            if (!HasRecords) return 0;
            return Records.Max(r => r.VideoMemoryUsage);
        }
    }

    [JsonIgnore]
    public double CpuBoundShare
    {
        get
        {
            if(!HasRecords) return 0;
            var percent = 100.0 * Records.Count(r => r.CPUFrameTime > r.GPUFrameTime) / Records.Count;
            return percent;
        }
    }

    [JsonIgnore]
    public double GpuBoundShare
    {
        get
        {
            if(!HasRecords) return 0;
            var percent = 100.0 * Records.Count(r => r.GPUFrameTime > r.CPUFrameTime) / Records.Count;
            return percent;
        }
    }

    public static BenchmarkResult? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<BenchmarkResult>(stream);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}