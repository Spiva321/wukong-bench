using System.Runtime.InteropServices;

namespace WukongBench;

/// <summary>
/// Эмуляция ввода средствами Windows (user32.dll).
///
/// Зачем она нужна. Benchmark Tool при запуске показывает заставку и меню. Просто запустить
/// процесс недостаточно: он просидит в меню, пока кто-то не нажмёт кнопку. Чтобы сценарий
/// был полностью автоматическим, мы сами «нажимаем».
///
/// SendInput работает только на Windows. В других системах все методы возвращают false
/// и ничего не делают — проверки на IsSupported стоят перед каждым обращением к API,
/// иначе вызов P/Invoke бросил бы исключение.
///
/// ⚠️ Координаты кнопок заданы долями от клиентской области окна. Если обновится бенчмарк
/// и кнопки переедут — правьте константы в BenchmarkRunner. Привязка к долям, а не к
/// пикселям, позволяет работать при любом разрешении окна.
/// </summary>
public sealed class NativeInput
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public const ushort VkReturn = 0x0D;
    private const int VirtualKeyLeftButton = 0x01;
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL, ParamH;
    }

    /// <summary>
    /// Объединение, которое принимает SendInput. Размер считает сама Windows —
    /// неверный размер означает, что ввод уйдёт мимо цели.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static IntPtr ForegroundWindow => IsSupported ? GetForegroundWindow() : IntPtr.Zero;

    /// <summary>Не нажимает ли сейчас пользователь левую кнопку мыши.</summary>
    public static bool IsMouseHeld => IsSupported && (GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0;

    /// <summary>
    /// Ставит окно на передний план. Без этого SendInput уходит не туда, и мы рискуем
    /// «нажать» что-нибудь в чужом приложении.
    /// </summary>
    public static bool TryActivate(IntPtr window)
    {
        if (!IsSupported || window == IntPtr.Zero) return false;

        ShowWindow(window, 9); // SW_RESTORE — на случай если окно свёрнуто
        return SetForegroundWindow(window);
    }

    public static bool IsForeground(IntPtr window) =>
        IsSupported && window != IntPtr.Zero && GetForegroundWindow() == window;

    /// <summary>
    /// Клик в точке окна, заданной долями клиентской области. Доли, а не пиксели:
    /// работает при любом разрешении окна.
    /// </summary>
    public static bool ClickRelative(IntPtr window, double xFraction, double yFraction)
    {
        if (!IsSupported || window == IntPtr.Zero) return false;
        if (!GetClientRect(window, out var rect)) return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        var point = new Point
        {
            X = (int)(width * xFraction),
            Y = (int)(height * yFraction),
        };

        // Клиентские координаты нужно перевести в экранные.
        if (!ClientToScreen(window, ref point)) return false;
        if (!SetCursorPos(point.X, point.Y)) return false;

        SendMouse(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(40);
        SendMouse(MOUSEEVENTF_LEFTUP);
        return true;
    }

    public static bool PressKey(ushort virtualKey)
    {
        if (!IsSupported) return false;

        SendKey(virtualKey, flags: 0);
        Thread.Sleep(30);
        SendKey(virtualKey, KEYEVENTF_KEYUP);
        return true;
    }

    /// <summary>
    /// Просит окно закрыться штатно (WM_CLOSE), а не убивает процесс.
    /// Бенчмарк при нормальном выходе дописывает файл результата — поэтому это
    /// единственный способ заставить его создать JSON.
    /// </summary>
    public static bool RequestClose(IntPtr window) =>
        IsSupported && window != IntPtr.Zero && PostMessage(window, WM_CLOSE, 0, 0);

    private static void SendMouse(uint flags)
    {
        var input = new Input
        {
            Type = INPUT_MOUSE,
            Union = new InputUnion { Mouse = new MouseInput { Flags = flags } }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendKey(ushort virtualKey, uint flags)
    {
        var input = new Input
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }
}