using System.Runtime.InteropServices;
using HUDMind.Core.Config;

namespace HUDMind.Windows;

public sealed class Win32GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
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
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly object _sync = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private Action? _onPause;
    private Action? _onAnalyze;
    private Action? _onToggleMic;
    private Action? _onQuickChat;

    public (bool PauseOk, bool AnalyzeOk, bool MicOk) Start(
        string pauseHotkey,
        string analyzeHotkey,
        string micHotkey,
        Action onPause,
        Action onAnalyze,
        Action? onToggleMic = null,
        Action<string>? onWarning = null)
    {
        var (p, a, m, _) = StartWithQuickChat(
            pauseHotkey, analyzeHotkey, micHotkey, string.Empty,
            onPause, onAnalyze, onToggleMic, null, onWarning);
        return (p, a, m);
    }

    public (bool PauseOk, bool AnalyzeOk, bool MicOk, bool QuickChatOk) StartWithQuickChat(
        string pauseHotkey,
        string analyzeHotkey,
        string micHotkey,
        string quickChatHotkey,
        Action onPause,
        Action onAnalyze,
        Action? onToggleMic = null,
        Action? onQuickChat = null,
        Action<string>? onWarning = null)
    {
        uint pMod = 0, pVk = 0x78;
        if (!HotkeyParser.TryParse(pauseHotkey, out pMod, out pVk, out _))
        {
            onWarning?.Invoke($"Tecla Pausar '{pauseHotkey}' inválida; usando F9 por defecto.");
            pMod = 0;
            pVk = 0x78;
        }

        uint aMod = 0, aVk = 0x79;
        if (!HotkeyParser.TryParse(analyzeHotkey, out aMod, out aVk, out _))
        {
            onWarning?.Invoke($"Tecla Analizar '{analyzeHotkey}' inválida; usando F10 por defecto.");
            aMod = 0;
            aVk = 0x79;
        }

        bool hasMic = !string.IsNullOrWhiteSpace(micHotkey) && onToggleMic is not null;
        uint mMod = 0, mVk = 0x77;
        if (hasMic && !HotkeyParser.TryParse(micHotkey, out mMod, out mVk, out _))
        {
            onWarning?.Invoke($"Tecla Mic '{micHotkey}' inválida; usando F8 por defecto.");
            mMod = 0;
            mVk = 0x77;
        }

        bool hasQuickChat = !string.IsNullOrWhiteSpace(quickChatHotkey) && onQuickChat is not null;
        uint qMod = 0, qVk = 0x76;
        if (hasQuickChat && !HotkeyParser.TryParse(quickChatHotkey, out qMod, out qVk, out _))
        {
            onWarning?.Invoke($"Tecla Chat Rápido '{quickChatHotkey}' inválida; usando F7 por defecto.");
            qMod = 0;
            qVk = 0x76;
        }

        bool pauseOk = false;
        bool analyzeOk = false;
        bool micOk = false;
        bool quickChatOk = false;

        lock (_sync)
        {
            StopLocked();
            _onPause = onPause;
            _onAnalyze = onAnalyze;
            _onToggleMic = onToggleMic;
            _onQuickChat = onQuickChat;

            var readyEv = new ManualResetEventSlim(false);
            int evOwners = 2;

            _thread = new Thread(() =>
            {
                _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                _threadId = GetCurrentThreadId();

                pauseOk = RegisterHotKey(IntPtr.Zero, 1, pMod | ModNoRepeat, pVk);
                if (!pauseOk)
                    onWarning?.Invoke($"No se pudo registrar tecla Pausar '{pauseHotkey}' (Win32 err={Marshal.GetLastWin32Error()} — otro programa la usa).");

                analyzeOk = RegisterHotKey(IntPtr.Zero, 2, aMod | ModNoRepeat, aVk);
                if (!analyzeOk)
                    onWarning?.Invoke($"No se pudo registrar tecla Analizar '{analyzeHotkey}' (Win32 err={Marshal.GetLastWin32Error()} — otro programa la usa).");

                if (hasMic)
                {
                    micOk = RegisterHotKey(IntPtr.Zero, 3, mMod | ModNoRepeat, mVk);
                    if (!micOk)
                        onWarning?.Invoke($"No se pudo registrar tecla Mic '{micHotkey}' (Win32 err={Marshal.GetLastWin32Error()}).");
                }

                if (hasQuickChat)
                {
                    quickChatOk = RegisterHotKey(IntPtr.Zero, 4, qMod | ModNoRepeat, qVk);
                    if (!quickChatOk)
                        onWarning?.Invoke($"No se pudo registrar tecla Chat Rápido '{quickChatHotkey}' (Win32 err={Marshal.GetLastWin32Error()}).");
                }

                readyEv.Set();
                if (Interlocked.Decrement(ref evOwners) == 0)
                    readyEv.Dispose();
                try
                {
                    while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        if (msg.message != WmHotkey) continue;
                        int id = (int)msg.wParam;
                        Action? target = id switch
                        {
                            1 => _onPause,
                            2 => _onAnalyze,
                            3 => _onToggleMic,
                            4 => _onQuickChat,
                            _ => null
                        };
                        if (target is null) continue;

                        // Dispatch asynchronously so GetMessage pump & Stop() never block on callbacks!
                        ThreadPool.UnsafeQueueUserWorkItem(static state =>
                        {
                            var (fn, warn) = ((Action, Action<string>?))state!;
                            try { fn(); }
                            catch (Exception ex) { warn?.Invoke($"Error en callback de hotkey: {ex.Message}"); }
                        }, (target, onWarning));
                    }
                }
                finally
                {
                    if (pauseOk) UnregisterHotKey(IntPtr.Zero, 1);
                    if (analyzeOk) UnregisterHotKey(IntPtr.Zero, 2);
                    if (micOk) UnregisterHotKey(IntPtr.Zero, 3);
                    if (quickChatOk) UnregisterHotKey(IntPtr.Zero, 4);
                }
            })
            {
                IsBackground = true,
                Name = "HUDMind-Win32-Hotkeys"
            };
            _thread.Start();
            readyEv.Wait(TimeSpan.FromSeconds(2));
            if (Interlocked.Decrement(ref evOwners) == 0)
                readyEv.Dispose();
            return (pauseOk, analyzeOk, micOk, quickChatOk);
        }
    }

    public void Stop()
    {
        lock (_sync)
            StopLocked();
    }

    private void StopLocked()
    {
        uint tid = _threadId;
        if (_thread is { IsAlive: true } && tid != 0)
        {
            PostThreadMessage(tid, WmQuit, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromMilliseconds(800));
        }
        _thread = null;
        _threadId = 0;
    }

    public static (uint Modifiers, uint VirtualKey) ParseHotkey(string? raw, uint fallbackVk) =>
        HotkeyParser.TryParse(raw, out uint mod, out uint vk, out _) ? (mod, vk) : (0, fallbackVk);

    public void Dispose() => Stop();
}

/// <summary>
/// Anti-cheat safe non-hook key state reader (no WH_KEYBOARD_LL hooks) for Push-to-Talk hold detection.
/// </summary>
public static class Win32KeyStateProbe
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsVirtualKeyDown(uint virtualKey) =>
        virtualKey != 0 && (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    public static bool IsPushToTalkKeyDown(string? keyName) =>
        HotkeyParser.TryParsePushToTalkKey(keyName, out uint vk, out _) && IsVirtualKeyDown(vk);
}
