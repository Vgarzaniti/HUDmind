using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace HUDMind.UiTests;

/// <summary>
/// Zero-NuGet Windows UI Automation + Win32 helper for driving real HUDMind.Native.exe
/// windows, dialogs, tabs, combos, and cross-process IPC flows end-to-end.
/// </summary>
internal static class UiAutomationDriver
{
    public static string LocateAppExecutable()
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "HUDMind.App", "bin", "Release", "net9.0-windows10.0.19041.0", "HUDMind.Native.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "HUDMind.App", "bin", "Debug", "net9.0-windows10.0.19041.0", "HUDMind.Native.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "HUDMind.Native.exe"))
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            $"HUDMind.Native.exe not found. Looked in: {string.Join("; ", candidates)}");
    }

    public static Process LaunchIsolatedApp(
        string exePath,
        string dataDir,
        params string[] extraArgs)
    {
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "profiles"));
        Directory.CreateDirectory(Path.Combine(dataDir, "config"));

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = dataDir,
            UseShellExecute = false
        };
        psi.Environment["HUDMIND_HOME"] = dataDir;
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--no-update-check");
        foreach (var arg in extraArgs)
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start HUDMind.Native.exe.");
        try
        {
            proc.WaitForInputIdle(5000);
        }
        catch
        {
            // Ignore WaitForInputIdle races during early startup
        }
        return proc;
    }

    public static AutomationElement WaitForWindow(
        int processId,
        string automationId,
        AutomationElement? parentWindow = null,
        int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var found = TryFindWindow(processId, automationId, parentWindow);
            if (found is not null)
                return found;
            Thread.Sleep(75);
        }

        throw new TimeoutException(
            $"Timed out after {timeoutMs}ms waiting for window AutomationId='{automationId}' (PID={processId}).");
    }

    public static void WaitUntilWindowGone(
        int processId,
        string automationId,
        AutomationElement? parentWindow = null,
        int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (TryFindWindow(processId, automationId, parentWindow) is null)
                return;
            Thread.Sleep(75);
        }

        throw new TimeoutException(
            $"Timed out after {timeoutMs}ms waiting for window AutomationId='{automationId}' to close.");
    }

    public static AutomationElement? TryFindWindow(
        int processId,
        string automationId,
        AutomationElement? parentWindow = null)
    {
        try
        {
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

            var el = AutomationElement.RootElement.FindFirst(TreeScope.Children, cond);
            if (el is not null)
                return el;

            if (parentWindow is not null)
            {
                el = parentWindow.FindFirst(TreeScope.Children, cond);
                if (el is not null)
                    return el;
            }

            foreach (var hwnd in EnumVisibleProcessWindows(processId))
            {
                try
                {
                    var winEl = AutomationElement.FromHandle(hwnd);
                    if (string.Equals(winEl.Current.AutomationId, automationId, StringComparison.Ordinal))
                        return winEl;
                }
                catch
                {
                    // Transient window teardown
                }
            }
        }
        catch
        {
            // Retry cleanly while tree stabilizes
        }
        return null;
    }

    public static AutomationElement WaitForElement(
        AutomationElement root,
        string automationId,
        int timeoutMs = 8000,
        bool requireEnabled = true)
    {
        var sw = Stopwatch.StartNew();
        var cond = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var el = root.FindFirst(TreeScope.Descendants, cond);
                if (el is not null && (!requireEnabled || el.Current.IsEnabled))
                    return el;
            }
            catch
            {
                // Transient UI update
            }
            Thread.Sleep(60);
        }

        throw new TimeoutException(
            $"Timed out waiting for AutomationId='{automationId}' inside '{SafeName(root)}'.");
    }

    public static string WaitForNonEmptyText(
        AutomationElement textElement,
        Func<string, bool> predicate,
        string description,
        int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        string lastSeen = string.Empty;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lastSeen = ReadText(textElement);
            if (predicate(lastSeen))
                return lastSeen;
            Thread.Sleep(60);
        }

        throw new TimeoutException(
            $"Timed out waiting for {description}. Last observed text: '{lastSeen}'.");
    }

    public static string ReadText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) &&
                vpObj is ValuePattern vp)
            {
                return vp.Current.Value ?? string.Empty;
            }
        }
        catch
        {
            // Fall through to Name property
        }

        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void SetText(AutomationElement element, string value)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) ||
            patternObj is not ValuePattern vp)
        {
            throw new InvalidOperationException(
                $"Element '{SafeName(element)}' does not support ValuePattern.");
        }
        vp.SetValue(value);
    }

    public static void Invoke(AutomationElement button)
    {
        ScrollIntoViewIfSupported(button);
        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObj) ||
            patternObj is not InvokePattern ip)
        {
            throw new InvalidOperationException(
                $"Element '{SafeName(button)}' does not support InvokePattern.");
        }
        ip.Invoke();
    }

    public static void ScrollIntoViewIfSupported(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var spObj) &&
                spObj is ScrollItemPattern sip)
            {
                sip.ScrollIntoView();
                Thread.Sleep(40);
            }
        }
        catch
        {
            // Control may not reside inside a virtualized/scrolling container
        }
    }

    public static void SelectRadioOrTab(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var spObj) &&
            spObj is SelectionItemPattern sp)
        {
            sp.Select();
            return;
        }
        Invoke(element);
    }

    public static void SelectComboBoxItemContains(
        AutomationElement mainRoot,
        int processId,
        AutomationElement comboBox,
        string textSubstring,
        int timeoutMs = 6000)
    {
        if (!comboBox.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expObj) ||
            expObj is not ExpandCollapsePattern exp)
        {
            throw new InvalidOperationException("ComboBox does not support ExpandCollapsePattern.");
        }

        exp.Expand();
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var item = FindComboItemInScope(comboBox, textSubstring)
                    ?? FindComboItemInProcessPopups(mainRoot, processId, textSubstring);
                if (item is not null &&
                    item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj) &&
                    selObj is SelectionItemPattern sel)
                {
                    sel.Select();
                    Thread.Sleep(150);
                    return;
                }
                Thread.Sleep(60);
            }

            throw new TimeoutException(
                $"Could not find ComboBox item matching '{textSubstring}'.");
        }
        finally
        {
            try
            {
                if (exp.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                    exp.Collapse();
            }
            catch
            {
                // Selecting often auto-collapses WPF ComboBox
            }
        }
    }

    public static void CloseWindow(AutomationElement window)
    {
        if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var wpObj) &&
            wpObj is WindowPattern wp)
        {
            wp.Close();
            return;
        }
        throw new InvalidOperationException($"Window '{SafeName(window)}' does not support WindowPattern.");
    }

    public static void TypeUnicodeIntoPasswordControl(
        AutomationElement dialogWindow,
        AutomationElement passwordBox,
        int processId,
        string text)
    {
        EnsureForegroundFocus(dialogWindow, passwordBox, processId);

        IntPtr dialogHwnd = new(dialogWindow.Current.NativeWindowHandle);
        if (dialogHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Dialog window has no valid native HWND.");

        foreach (char ch in text)
            SendMessageW(dialogHwnd, WM_CHAR, (IntPtr)ch, (IntPtr)1);

        Thread.Sleep(100);
    }

    private static void EnsureForegroundFocus(
        AutomationElement dialogWindow,
        AutomationElement targetControl,
        int processId)
    {
        IntPtr hwnd = new(dialogWindow.Current.NativeWindowHandle);
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2500)
        {
            try
            {
                targetControl.SetFocus();
                Thread.Sleep(70);
                var focused = AutomationElement.FocusedElement;
                if (focused is not null &&
                    focused.Current.ProcessId == processId &&
                    string.Equals(focused.Current.AutomationId, targetControl.Current.AutomationId, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch
            {
                // Retry focus handshake
            }
            Thread.Sleep(60);
        }
    }

    private static AutomationElement? FindComboItemInScope(AutomationElement scope, string textSubstring)
    {
        try
        {
            var items = scope.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            foreach (AutomationElement item in items)
            {
                var name = item.Current.Name ?? string.Empty;
                if (name.Contains(textSubstring, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
        }
        catch
        {
            // Ignore transient enumeration exceptions
        }
        return null;
    }

    private static AutomationElement? FindComboItemInProcessPopups(
        AutomationElement mainRoot,
        int processId,
        string textSubstring)
    {
        // Search popup windows owned by the process under Desktop AND MainWindow
        var procCond = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        foreach (var root in new[] { AutomationElement.RootElement, mainRoot })
        {
            try
            {
                var children = root.FindAll(TreeScope.Children, procCond);
                foreach (AutomationElement child in children)
                {
                    var match = FindComboItemInScope(child, textSubstring);
                    if (match is not null)
                        return match;
                }
            }
            catch
            {
                // Ignore transient popup teardown
            }
        }
        return null;
    }

    private static string SafeName(AutomationElement el)
    {
        try { return !string.IsNullOrEmpty(el.Current.AutomationId) ? el.Current.AutomationId : el.Current.Name; }
        catch { return "<unknown>"; }
    }

    private static List<IntPtr> EnumVisibleProcessWindows(int targetPid)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)targetPid)
                list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"UI E2E Assertion Failed: {message}");
    }

    private const uint WM_CHAR = 0x0102;
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
