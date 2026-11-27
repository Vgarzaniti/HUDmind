using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HUDMind.Core.Config;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace HUDMind.App.Services;

/// <summary>
/// Pure-WPF focus-scoped key & mouse-side-button capture helper for hotkey TextBoxes.
/// Zero global keyboard/mouse hooks (WH_KEYBOARD_LL/WH_MOUSE_LL) — captures input strictly
/// while the TextBox has WPF keyboard focus.
/// </summary>
public static class HotkeyRecorderHelper
{
    private static readonly MediaSolidColorBrush RecordingBorder = CreateFrozen("#22D3EE");
    private static readonly MediaSolidColorBrush RecordingBackground = CreateFrozen("#0D2538");
    private static readonly MediaSolidColorBrush NormalBorder = CreateFrozen("#1A2C40");
    private static readonly MediaSolidColorBrush NormalBackground = CreateFrozen("#09111B");

    public static void AttachStandardHotkey(TextBox box, Action<string>? onCaptured = null)
    {
        AttachVisualFocusCue(box, "Hacé clic y presioná la tecla o combo (ej: Ctrl+M, F9). Esc cancela.");

        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
                return;
            }
            if (key == Key.Tab)
                return;

            e.Handled = true;
            if (IsModifierKey(key))
                return;

            var candidate = FormatKeyWithModifiers(key, Keyboard.Modifiers);
            if (HotkeyParser.TryParse(candidate, out uint _, out uint _, out var normalized))
            {
                box.Text = normalized;
                onCaptured?.Invoke(normalized);
                Keyboard.ClearFocus();
            }
        };
    }

    public static void AttachPushToTalkRecorder(TextBox box, Action<string>? onCaptured = null)
    {
        AttachVisualFocusCue(box, "Hacé clic y tocá tu tecla o botón lateral del mouse (Mouse4/Mouse5/CapsLock/V). Esc cancela.");

        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
                return;
            }
            if (key == Key.Tab)
                return;

            e.Handled = true;
            var candidate = MapSinglePttKey(key);
            if (!string.IsNullOrEmpty(candidate) && HotkeyParser.TryParsePushToTalkKey(candidate, out uint _, out var normalized))
            {
                box.Text = normalized;
                onCaptured?.Invoke(normalized);
                Keyboard.ClearFocus();
            }
        };

        box.PreviewMouseDown += (_, e) =>
        {
            if (!box.IsKeyboardFocused)
                return;

            string? candidate = e.ChangedButton switch
            {
                MouseButton.XButton1 => "Mouse4",
                MouseButton.XButton2 => "Mouse5",
                _ => null
            };
            if (candidate is not null && HotkeyParser.TryParsePushToTalkKey(candidate, out uint _, out var normalized))
            {
                e.Handled = true;
                box.Text = normalized;
                onCaptured?.Invoke(normalized);
                Keyboard.ClearFocus();
            }
        };
    }

    private static void AttachVisualFocusCue(TextBox box, string hint)
    {
        box.IsReadOnly = true;
        box.Cursor = System.Windows.Input.Cursors.Hand;
        if (string.IsNullOrWhiteSpace(box.ToolTip as string))
            box.ToolTip = hint;

        box.GotKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = RecordingBorder;
            box.Background = RecordingBackground;
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = NormalBorder;
            box.Background = NormalBackground;
        };
    }

    private static string FormatKeyWithModifiers(Key key, ModifierKeys modifiers)
    {
        var token = MapStandardKeyToken(key);
        if (string.IsNullOrEmpty(token))
            return string.Empty;

        var sb = new StringBuilder();
        if ((modifiers & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((modifiers & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((modifiers & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        sb.Append(token);
        return sb.ToString();
    }

    private static string MapStandardKeyToken(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24)
            return $"F{(int)key - (int)Key.F1 + 1}";
        if (key is >= Key.A and <= Key.Z)
            return key.ToString();
        if (key is >= Key.D0 and <= Key.D9)
            return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return $"NUM{(int)key - (int)Key.NumPad0}";

        return key switch
        {
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Pause => "Pause",
            Key.Space => "Space",
            _ => string.Empty
        };
    }

    private static string MapSinglePttKey(Key key)
    {
        var std = MapStandardKeyToken(key);
        if (!string.IsNullOrEmpty(std))
            return std;

        return key switch
        {
            Key.CapsLock => "CapsLock",
            Key.Oem3 => "Backquote",
            Key.LeftAlt => "Alt",
            Key.RightAlt => "RAlt",
            Key.LeftCtrl => "Ctrl",
            Key.RightCtrl => "RCtrl",
            Key.LeftShift => "Shift",
            Key.RightShift => "RShift",
            _ => string.Empty
        };
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static MediaSolidColorBrush CreateFrozen(string hex)
    {
        var b = new MediaSolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
