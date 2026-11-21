using System.Runtime.InteropServices;

namespace HUDMind.Windows;

public sealed class Win32GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private Thread? _thread;
    private uint _threadId;
    private Action? _onPause;
    private Action? _onAnalyze;

    public void Start(string pauseHotkey, string analyzeHotkey, Action onPause, Action onAnalyze)
    {
        Stop();
        _onPause = onPause;
        _onAnalyze = onAnalyze;

        var (pMod, pVk) = ParseHotkey(pauseHotkey, 0x78);   // default F9
        var (aMod, aVk) = ParseHotkey(analyzeHotkey, 0x79); // default F10

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            RegisterHotKey(IntPtr.Zero, 1, pMod | ModNoRepeat, pVk);
            RegisterHotKey(IntPtr.Zero, 2, aMod | ModNoRepeat, aVk);
            try
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WmHotkey)
                    {
                        int id = (int)msg.wParam;
                        if (id == 1) _onPause?.Invoke();
                        else if (id == 2) _onAnalyze?.Invoke();
                    }
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, 1);
                UnregisterHotKey(IntPtr.Zero, 2);
            }
        })
        {
            IsBackground = true,
            Name = "HUDMind-Win32-Hotkeys"
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is { IsAlive: true } && _threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromMilliseconds(500));
        }
        _thread = null;
        _threadId = 0;
    }

    public static (uint Modifiers, uint VirtualKey) ParseHotkey(string? raw, uint fallbackVk)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (0, fallbackVk);

        uint mods = 0;
        uint vk = fallbackVk;
        foreach (var token in raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = token.ToUpperInvariant();
            if (t is "CTRL" or "CONTROL") mods |= ModControl;
            else if (t == "ALT") mods |= ModAlt;
            else if (t == "SHIFT") mods |= ModShift;
            else if (t.StartsWith('F') && int.TryParse(t[1..], out int fNum) && fNum is >= 1 and <= 24)
                vk = (uint)(0x70 + (fNum - 1));
            else if (t.Length == 1 && char.IsLetterOrDigit(t[0]))
                vk = t[0];
        }
        return (mods, vk);
    }

    public void Dispose() => Stop();
}
