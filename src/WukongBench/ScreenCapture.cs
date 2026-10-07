using System.Drawing;
using System.Drawing.Imaging;

namespace WukongBench;

/// <summary>
/// Скриншоты для диагностики: снимает весь экран, чтобы понять, на каком экране
/// бенчмарк остановился. Только для отладки — в готовом прогоне не участвует.
///
/// Зачем это нужно. Инструмент нажимает кнопки вслепую, по координатам. Если кнопка
/// не нажалась, он не знает об этом: клики уходят в пустоту, а бенчмарг висит на меню.
/// Скриншот показывает, что на самом деле на экране.
/// </summary>
public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Снимает виртуальный экран целиком и сохраняет в PNG.
    /// Возвращает путь или null, если снять не удалось.
    /// </summary>
    public static string? Capture(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0) return null;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.ExternalException
                                      or NotSupportedException)
        {
            return null;
        }
    }
}