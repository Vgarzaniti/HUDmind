using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HUDMind.App.Services;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;
using HUDMind.Windows;
using Microsoft.Win32;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace HUDMind.App;

public sealed class OverlayFeedRowViewModel : INotifyPropertyChanged
{
    private string _speakerLabel = string.Empty;
    private string _text = string.Empty;
    private Visibility _speakerVisibility = Visibility.Collapsed;
    private MediaBrush _accentBrush = HudVisualPalette.HeaderCyan;
    private MediaBrush _speakerBrush = HudVisualPalette.SpeakerStatusCyan;
    private MediaBrush _messageBrush = HudVisualPalette.TextWhitePrimary;
    private double _speakerFontSize = 9.5;
    private double _messageFontSize = 14.0;
    private bool _strongHalo;
    private double _rowOpacity = 1.0;
    private Thickness _rowMargin = new(0);

    public long EntryId { get; init; }
    public OverlayEntryKind Kind { get; set; }
    public UrgencyLevel Urgency { get; set; }

    public string SpeakerLabel
    {
        get => _speakerLabel;
        set => SetField(ref _speakerLabel, value);
    }

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public Visibility SpeakerVisibility
    {
        get => _speakerVisibility;
        set => SetField(ref _speakerVisibility, value);
    }

    public MediaBrush AccentBrush
    {
        get => _accentBrush;
        set => SetField(ref _accentBrush, value);
    }

    public MediaBrush SpeakerBrush
    {
        get => _speakerBrush;
        set => SetField(ref _speakerBrush, value);
    }

    public MediaBrush MessageBrush
    {
        get => _messageBrush;
        set => SetField(ref _messageBrush, value);
    }

    public double SpeakerFontSize
    {
        get => _speakerFontSize;
        set => SetField(ref _speakerFontSize, value);
    }

    public double MessageFontSize
    {
        get => _messageFontSize;
        set => SetField(ref _messageFontSize, value);
    }

    public bool StrongHalo
    {
        get => _strongHalo;
        set => SetField(ref _strongHalo, value);
    }

    public double RowOpacity
    {
        get => _rowOpacity;
        set => SetField(ref _rowOpacity, value);
    }

    public Thickness RowMargin
    {
        get => _rowMargin;
        set => SetField(ref _rowMargin, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class OverlayWindow : Window, IOverlayOutputService
{
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr hwnd, int index, nint newStyle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly DispatcherTimer _pruneTimer;
    private readonly DispatcherTimer _topmostGuardTimer;
    private readonly OverlayFeedBuffer _feedBuffer = new();
    private readonly ObservableCollection<OverlayFeedRowViewModel> _rows = [];

    private MonitorBoundsRect[] _cachedWorkingAreas = [];
    private bool _screensDirty = true;
    private string _position = "top-right";
    private int _monitorIndex = 1;
    private bool _followActiveMonitor = true;
    private bool _desktopOverlayEnabled = true;
    private int? _autoMonitorIndex;
    private double? _customLeft;
    private double? _customTop;
    private bool _dragMode;
    private double _backdropOpacity = 0.55;
    private double _scale = 1.0;
    private string _style = UserConfig.OverlayStyleDiscordPill;
    private int _maxMessages = 3;
    private UrgencyLevel _activeHeaderUrgency = UrgencyLevel.Low;
    private string _activeHeaderTitle = "HUDMIND";
    private bool _closed;
    private double _lastActualH;
    private string _lastPosKey = string.Empty;

    private bool _quickChatOpen;
    private IntPtr _returnFocusHwnd;
    private string _quickChatKeyLabel = "F7";

    private int _voiceUiPending;
    private volatile bool _latestUserMicActive;
    private double _latestUserLevel01;
    private volatile bool _latestCoachVoiceActive;
    private double _latestCoachLevel01;
    private long _lastPttToastTick;

    private string _cachedThemeKey = string.Empty;
    private MediaBrush? _cachedOuterBrush;
    private MediaBrush? _cachedCardBgBrush;
    private MediaBrush? _cachedCardBorderBrush;
    private MediaBrush? _cachedStatusBgBrush;
    private MediaBrush? _cachedStatusBorderBrush;

    public event Action<string>? QuickChatSubmitted;
    public event Action<double, double>? CustomPositionCommitted;
    public bool IsQuickChatOpen => _quickChatOpen;
    public bool IsDragPositionMode => _dragMode;

    public OverlayWindow()
    {
        InitializeComponent();
        FeedItemsHost.ItemsSource = _rows;
        OverlayWave.SetBarHeightScale(18);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        MouseLeftButtonDown += OnOverlayMouseLeftButtonDown;

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pruneTimer.Tick += (_, _) => OnPruneTimerTick();

        _topmostGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _topmostGuardTimer.Tick += (_, _) =>
        {
            if (_closed || !IsVisible || _quickChatOpen || _dragMode)
            {
                _topmostGuardTimer.Stop();
                return;
            }
            ReassertTopmostAndActiveMonitor();
        };

        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_closed && !_dragMode && Math.Abs(ActualHeight - _lastActualH) > 1.0)
                PositionOnScreen(force: true);
        };
        Deactivated += (_, _) =>
        {
            if (_quickChatOpen && !_closed)
                CloseQuickChat(restoreFocus: false);
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _screensDirty = true;
        _lastPosKey = string.Empty;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThroughStyle(clickThrough: !_quickChatOpen && !_dragMode);
    }

    public void SetDragPositionMode(bool enabled)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _dragMode = enabled;
            Cursor = enabled ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow;
            ApplyClickThroughStyle(clickThrough: !_quickChatOpen && !_dragMode);

            if (enabled)
            {
                _pruneTimer.Stop();
                _topmostGuardTimer.Stop();
                if (_feedBuffer.IsEmpty)
                {
                    var snapshot = _feedBuffer.Upsert(
                        new OverlayCalloutMessage(
                            AppLocalizationService.T(
                                "📌 Arrastrá este cartel con el mouse hasta donde te resulte cómodo y luego tocá '🔒 Fijar posición'.",
                                "📌 Drag this overlay anywhere with your mouse, then click '🔒 Lock Position'."),
                            UrgencyLevel.Low,
                            AppLocalizationService.T("HUDMind · Posición", "HUDMind · Position"),
                            30.0,
                            OverlayEntryKind.Status,
                            "HUD"),
                        DateTimeOffset.UtcNow);
                    _activeHeaderUrgency = UrgencyLevel.Low;
                    _activeHeaderTitle = AppLocalizationService.T("HUDMind · Posición", "HUDMind · Position");
                    SyncRowsFromSnapshot(snapshot);
                }

                _cachedThemeKey = string.Empty;
                ApplyVisualTheme(_activeHeaderUrgency, _activeHeaderTitle);
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
                if (!IsVisible)
                    Show();
                UpdateLayout();
                PositionOnScreen(force: true);
            }
            else
            {
                _cachedThemeKey = string.Empty;
                ApplyVisualTheme(_activeHeaderUrgency, _activeHeaderTitle);
                if (_feedBuffer.IsEmpty)
                {
                    if (IsVisible && !_quickChatOpen)
                    {
                        BeginAnimation(OpacityProperty, null);
                        Hide();
                    }
                }
                else
                {
                    ScheduleNextPrune(DateTimeOffset.UtcNow);
                    if (!_topmostGuardTimer.IsEnabled)
                        _topmostGuardTimer.Start();
                }
            }
        });
    }

    private void OnOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_dragMode || _closed || _quickChatOpen || e.ChangedButton != MouseButton.Left)
            return;
        try
        {
            DragMove();
        }
        catch
        {
            return;
        }

        var (clampedLeft, clampedTop) = ClampOverlayDipCoords(Left, Top, Width, Math.Max(52, ActualHeight));
        Left = clampedLeft;
        Top = clampedTop;
        _customLeft = clampedLeft;
        _customTop = clampedTop;
        _position = "custom";
        _lastPosKey = string.Empty;
        CustomPositionCommitted?.Invoke(clampedLeft, clampedTop);
        e.Handled = true;
    }

    private static (double Left, double Top) ClampOverlayDipCoords(double left, double top, double width, double height)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vW = Math.Max(640, SystemParameters.VirtualScreenWidth);
        double vH = Math.Max(480, SystemParameters.VirtualScreenHeight);
        double maxL = vLeft + Math.Max(0, vW - Math.Max(160, width));
        double maxT = vTop + Math.Max(0, vH - Math.Max(48, height));
        return (Math.Clamp(left, vLeft, maxL), Math.Clamp(top, vTop, maxT));
    }

    private void ApplyClickThroughStyle(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (IntPtr.Size == 8)
        {
            nint exStyle = GetWindowLongPtr64(hwnd, GwlExStyle) | WsExToolWindow | WsExTopmost;
            exStyle = clickThrough
                ? exStyle | WsExTransparent | WsExNoActivate
                : exStyle & ~(nint)(WsExTransparent | WsExNoActivate);
            SetWindowLongPtr64(hwnd, GwlExStyle, exStyle);
        }
        else
        {
            int exStyle = GetWindowLong32(hwnd, GwlExStyle) | WsExToolWindow | WsExTopmost;
            exStyle = clickThrough
                ? exStyle | WsExTransparent | WsExNoActivate
                : exStyle & ~(WsExTransparent | WsExNoActivate);
            SetWindowLong32(hwnd, GwlExStyle, exStyle);
        }
    }

    public void SetHotkeyHints(string pauseKey, string analyzeKey, string? quickChatKey = null)
    {
        if (_closed) return;
        var p = string.IsNullOrWhiteSpace(pauseKey) ? "F9" : pauseKey.Trim().ToUpperInvariant();
        var a = string.IsNullOrWhiteSpace(analyzeKey) ? "F10" : analyzeKey.Trim().ToUpperInvariant();
        var q = string.IsNullOrWhiteSpace(quickChatKey) ? "F7" : quickChatKey.Trim().ToUpperInvariant();
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            TxtPauseChip.Text = $"{p} {AppLocalizationService.T("PAUSA", "PAUSE")}";
            TxtAnalyzeChip.Text = $"{a} {AppLocalizationService.T("IA", "AI")}";
            _quickChatKeyLabel = q;
            TxtQuickChatPlaceholder.Text = AppLocalizationService.T(
                $"Preguntale al coach... (Enter = enviar · Esc/{_quickChatKeyLabel} = volver)",
                $"Ask your coach... (Enter = send · Esc/{_quickChatKeyLabel} = back)");
        });
    }

    public void ToggleQuickChat()
    {
        if (_closed) return;
        IntPtr externalHwnd = Win32WindowDetector.GetExternalForegroundWindowHandle();
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            if (_quickChatOpen)
            {
                CloseQuickChat(restoreFocus: true);
                return;
            }

            _returnFocusHwnd = externalHwnd;
            _quickChatOpen = true;
            _pruneTimer.Stop();

            if (_rows.Count == 0 && string.IsNullOrEmpty(_activeHeaderTitle))
                _activeHeaderTitle = "HUDMind";
            ApplyVisualTheme(_activeHeaderUrgency, _activeHeaderTitle);
            QuickChatDock.Visibility = Visibility.Visible;
            TxtQuickChatPlaceholder.Text = AppLocalizationService.T(
                $"Preguntale al coach... (Enter = enviar · Esc/{_quickChatKeyLabel} = volver)",
                $"Ask your coach... (Enter = send · Esc/{_quickChatKeyLabel} = back)");
            TxtQuickChatPlaceholder.Visibility = Visibility.Visible;

            if (!IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
                Show();
            }
            else
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
            }

            ApplyClickThroughStyle(clickThrough: false);
            UpdateLayout();
            PositionOnScreen(force: true);

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
                _ = SetForegroundWindow(hwnd);
            }
            Activate();
            TxtQuickChat.Focus();
            Keyboard.Focus(TxtQuickChat);
        });
    }

    private void CloseQuickChat(bool restoreFocus, bool submitText = false)
    {
        if (_closed) return;
        string submitted = submitText ? (TxtQuickChat.Text ?? string.Empty).Trim() : string.Empty;
        IntPtr returnTarget = _returnFocusHwnd;
        _returnFocusHwnd = IntPtr.Zero;
        _quickChatOpen = false;

        TxtQuickChat.Clear();
        QuickChatDock.Visibility = Visibility.Collapsed;
        ApplyClickThroughStyle(clickThrough: true);

        if (_feedBuffer.IsEmpty)
        {
            _pruneTimer.Stop();
            BeginAnimation(OpacityProperty, null);
            Hide();
        }
        else
        {
            UpdateLayout();
            PositionOnScreen(force: true);
            ScheduleNextPrune(DateTimeOffset.UtcNow);
        }

        if (restoreFocus && returnTarget != IntPtr.Zero)
            Win32WindowDetector.TryRestoreForegroundWindow(returnTarget);

        if (!string.IsNullOrEmpty(submitted))
            QuickChatSubmitted?.Invoke(submitted);
    }

    private void TxtQuickChat_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CloseQuickChat(restoreFocus: true, submitText: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseQuickChat(restoreFocus: true, submitText: false);
        }
    }

    private void TxtQuickChat_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtQuickChatPlaceholder is not null)
            TxtQuickChatPlaceholder.Visibility = string.IsNullOrEmpty(TxtQuickChat.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnQuickChatSend_Click(object sender, RoutedEventArgs e) =>
        CloseQuickChat(restoreFocus: true, submitText: true);

    private void BtnQuickChatClose_Click(object sender, RoutedEventArgs e) =>
        CloseQuickChat(restoreFocus: true, submitText: false);

    public void UpdateVoiceActivity(bool userMicActive, double userLevel01, bool coachVoiceActive, double coachLevel01)
    {
        if (_closed) return;
        _latestUserMicActive = userMicActive;
        Volatile.Write(ref _latestUserLevel01, userLevel01);
        _latestCoachVoiceActive = coachVoiceActive;
        Volatile.Write(ref _latestCoachLevel01, coachLevel01);

        if (userMicActive && !IsVisible && !_quickChatOpen)
        {
            long prevToast = Interlocked.Read(ref _lastPttToastTick);
            if (prevToast == 0 || Environment.TickCount64 - prevToast > 900)
            {
                Interlocked.Exchange(ref _lastPttToastTick, Environment.TickCount64);
                ShowMessage(new OverlayCalloutMessage(
                    AppLocalizationService.T("🎤 Te escucho...", "🎤 Listening..."),
                    UrgencyLevel.Low,
                    string.IsNullOrWhiteSpace(_activeHeaderTitle) ? "HUDMind Live" : _activeHeaderTitle,
                    1.8,
                    OverlayEntryKind.Status,
                    "HUD"));
            }
        }

        if (Interlocked.CompareExchange(ref _voiceUiPending, 1, 0) != 0)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _voiceUiPending, 0);
            if (_closed) return;
            ApplyVoiceActivityUi();
        });
    }

    private void ApplyVoiceActivityUi()
    {
        bool userOn = _latestUserMicActive;
        bool coachOn = _latestCoachVoiceActive;
        double uLvl = Volatile.Read(ref _latestUserLevel01);
        double cLvl = Volatile.Read(ref _latestCoachLevel01);

        if (userOn)
        {
            VoicePillText.Text = AppLocalizationService.T("🎤 ESCUCHANDO", "🎤 LISTENING");
            VoicePillText.Foreground = HudVisualPalette.MicLiveFgBrush;
            VoicePillBorder.Background = _style == UserConfig.OverlayStyleTransparentText
                ? MediaBrushes.Transparent
                : HudVisualPalette.MicLiveBgBrush;
            VoicePillBorder.BorderBrush = _style == UserConfig.OverlayStyleTransparentText
                ? MediaBrushes.Transparent
                : HudVisualPalette.MicLiveBorderBrush;
            VoicePillBorder.BorderThickness = new Thickness(_style == UserConfig.OverlayStyleTransparentText ? 0 : 1);
            VoicePillBorder.Visibility = Visibility.Visible;
        }
        else if (coachOn)
        {
            VoicePillText.Text = AppLocalizationService.T("🔊 VOZ", "🔊 VOICE");
            VoicePillText.Foreground = HudVisualPalette.CoachVoiceFgBrush;
            VoicePillBorder.Background = _style == UserConfig.OverlayStyleTransparentText
                ? MediaBrushes.Transparent
                : HudVisualPalette.CoachVoiceBgBrush;
            VoicePillBorder.BorderBrush = _style == UserConfig.OverlayStyleTransparentText
                ? MediaBrushes.Transparent
                : HudVisualPalette.CoachVoiceBorderBrush;
            VoicePillBorder.BorderThickness = new Thickness(_style == UserConfig.OverlayStyleTransparentText ? 0 : 1);
            VoicePillBorder.Visibility = Visibility.Visible;
        }
        else
        {
            VoicePillBorder.Visibility = Visibility.Collapsed;
        }

        if (_style == UserConfig.OverlayStyleCard)
        {
            double peak = Math.Max(userOn ? uLvl : 0.0, coachOn ? cLvl : 0.0);
            OverlayWave.UpdateAudioLevel(peak);
        }
    }

    public void Configure(
        string position,
        int width,
        double opacity,
        int monitorIndex = 1,
        double scale = 1.0,
        bool compact = false,
        string? style = null,
        int maxMessages = 3,
        bool followActiveMonitor = true,
        bool desktopOverlayEnabled = true,
        double? customLeft = null,
        double? customTop = null)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _position = string.IsNullOrWhiteSpace(position) ? "top-right" : position.ToLowerInvariant();
            _monitorIndex = Math.Max(1, monitorIndex);
            _followActiveMonitor = followActiveMonitor;
            _desktopOverlayEnabled = desktopOverlayEnabled;
            _customLeft = customLeft;
            _customTop = customTop;
            if (!followActiveMonitor)
                _autoMonitorIndex = null;
            _scale = Math.Clamp(scale, 0.75, 1.45);
            _backdropOpacity = Math.Clamp(opacity, 0.0, 0.95);
            _style = UserConfig.NormalizeOverlayStyle(style, compactFallback: compact);
            _maxMessages = Math.Clamp(maxMessages, 1, 4);
            _feedBuffer.MaxMessages = _maxMessages;

            double baseW = _style switch
            {
                UserConfig.OverlayStyleTransparentText => Math.Max(340, width * 0.95),
                UserConfig.OverlayStyleDiscordPill => Math.Max(360, width * 0.92),
                _ => Math.Max(width, 456)
            };
            Width = Math.Clamp(baseW * _scale, 280, 760);

            _cachedThemeKey = string.Empty;
            ApplyVisualTheme(_activeHeaderUrgency, _activeHeaderTitle);
            SyncRowsFromSnapshot(_feedBuffer.Snapshot());

            if (IsVisible)
            {
                if (_rows.Count == 0 && !_quickChatOpen && !_dragMode)
                {
                    _pruneTimer.Stop();
                    _topmostGuardTimer.Stop();
                    BeginAnimation(OpacityProperty, null);
                    Hide();
                    return;
                }
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
                UpdateLayout();
                PositionOnScreen(force: true);
            }
        });
    }

    public void ClearHistory()
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _pruneTimer.Stop();
            _topmostGuardTimer.Stop();
            _feedBuffer.Clear();
            _rows.Clear();
            _latestUserMicActive = false;
            _latestCoachVoiceActive = false;
            ApplyVoiceActivityUi();
            if (_quickChatOpen)
                return;
            if (IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Hide();
            }
        });
    }

    public void HideImmediately()
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _pruneTimer.Stop();
            _topmostGuardTimer.Stop();
            if (_quickChatOpen)
                CloseQuickChat(restoreFocus: false);
            BeginAnimation(OpacityProperty, null);
            Hide();
        });
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds) =>
        ShowMessage(new OverlayCalloutMessage(text, urgency, title, durationSeconds, OverlayEntryKind.CoachFinal));

    public void ShowMessage(OverlayCalloutMessage message)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;

            var nowUtc = DateTimeOffset.UtcNow;
            var snapshot = _feedBuffer.Upsert(message, nowUtc);
            if (snapshot.Count == 0)
                return;

            var latest = snapshot[^1];
            _activeHeaderUrgency = latest.Urgency;
            _activeHeaderTitle = latest.GameTitle;

            ApplyVisualTheme(_activeHeaderUrgency, _activeHeaderTitle);
            SyncRowsFromSnapshot(snapshot);
            ApplyVoiceActivityUi();

            bool wasVisible = IsVisible;
            if (!_desktopOverlayEnabled && !_quickChatOpen)
            {
                if (wasVisible)
                {
                    _pruneTimer.Stop();
                    _topmostGuardTimer.Stop();
                    BeginAnimation(OpacityProperty, null);
                    Hide();
                }
                return;
            }
            if (!wasVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = _quickChatOpen ? 1.0 : 0.0;
                Show();
            }

            UpdateLayout();
            ReassertTopmostAndActiveMonitor(forceReposition: !wasVisible);

            if (!wasVisible && !_quickChatOpen)
            {
                var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120));
                Timeline.SetDesiredFrameRate(anim, 24);
                BeginAnimation(OpacityProperty, anim);
            }
            else
            {
                BeginAnimation(OpacityProperty, null);
                if (Math.Abs(Opacity - 1.0) > 0.01)
                    Opacity = 1.0;
            }

            if (!_quickChatOpen && !_dragMode)
            {
                ScheduleNextPrune(nowUtc);
                if (!_topmostGuardTimer.IsEnabled)
                    _topmostGuardTimer.Start();
            }
        });
    }

    private void OnPruneTimerTick()
    {
        if (_closed || _quickChatOpen || _dragMode)
        {
            _pruneTimer.Stop();
            return;
        }

        _pruneTimer.Stop();
        var nowUtc = DateTimeOffset.UtcNow;
        if (_feedBuffer.PruneExpired(nowUtc))
        {
            var snapshot = _feedBuffer.Snapshot();
            if (snapshot.Count == 0)
            {
                _rows.Clear();
                FadeOutAndHide();
                return;
            }

            var latest = snapshot[^1];
            _activeHeaderUrgency = latest.Urgency;
            _activeHeaderTitle = latest.GameTitle;
            ApplyVisualTheme(_activeHeaderUrgency, _activeHeaderTitle);
            SyncRowsFromSnapshot(snapshot);
            UpdateLayout();
            PositionOnScreen(force: true);
        }

        if (!_feedBuffer.IsEmpty)
            ScheduleNextPrune(nowUtc);
    }

    private void ScheduleNextPrune(DateTimeOffset nowUtc)
    {
        if (_dragMode || _quickChatOpen)
        {
            _pruneTimer.Stop();
            return;
        }
        var nextUtc = _feedBuffer.GetNextExpirationUtc();
        if (nextUtc is null)
        {
            _pruneTimer.Stop();
            return;
        }

        double msUntil = (nextUtc.Value - nowUtc).TotalMilliseconds;
        double clampedMs = Math.Clamp(msUntil + 40.0, 80.0, 20500.0);
        _pruneTimer.Stop();
        _pruneTimer.Interval = TimeSpan.FromMilliseconds(clampedMs);
        _pruneTimer.Start();
    }

    private void ApplyVisualTheme(UrgencyLevel urgency, string gameTitle)
    {
        var visual = HudVisualPalette.ResolveUrgency(urgency);
        bool isTransparentStyle = _style == UserConfig.OverlayStyleTransparentText;
        bool isCardStyle = _style == UserConfig.OverlayStyleCard;
        bool strongHalo = isTransparentStyle || _backdropOpacity < 0.30;

        IconBadge.Visibility = isCardStyle ? Visibility.Visible : Visibility.Collapsed;
        FooterDock.Visibility = isCardStyle ? Visibility.Visible : Visibility.Collapsed;

        TitleBlock.FontSize = (isTransparentStyle ? 10.0 : 10.5) * _scale;
        TitleBlock.StrongHalo = strongHalo;
        TitleBlock.Text = string.IsNullOrWhiteSpace(gameTitle)
            ? (isCardStyle ? "AI COPILOT · HUDMIND" : "HUDMIND")
            : isCardStyle
                ? $"AI COPILOT · {gameTitle.ToUpperInvariant()}"
                : $"HUDMIND · {gameTitle.ToUpperInvariant()}";

        StatusTagText.FontSize = 9.5 * _scale;
        StatusTagText.StrongHalo = strongHalo;
        StatusTagText.Foreground = visual.TagForegroundBrush;
        StatusTagText.Text = AppLocalizationService.IsEnglish
            ? urgency switch
            {
                UrgencyLevel.High => "URGENT",
                UrgencyLevel.Medium => "TACTICAL",
                _ => "ACTIVE"
            }
            : visual.TagText;

        VoicePillText.FontSize = 9.5 * _scale;
        VoicePillText.StrongHalo = strongHalo;

        if (isCardStyle)
            OverlayWave.SetBarHeightScale(Math.Round(18.0 * _scale));

        int alphaPct = (int)Math.Round(_backdropOpacity * 100);
        string themeKey = $"{_style}:{alphaPct}:{urgency}:{(_dragMode ? 1 : 0)}";
        if (themeKey == _cachedThemeKey)
            return;
        _cachedThemeKey = themeKey;

        if (isTransparentStyle)
        {
            OuterShadowFrame.Background = MediaBrushes.Transparent;
            OuterShadowFrame.Padding = new Thickness(0);
            CardBorder.Background = _dragMode
                ? HudVisualPalette.CreateFrozenBrush(0x08, 0x26, 0x36, 150)
                : MediaBrushes.Transparent;
            CardBorder.BorderBrush = _dragMode
                ? HudVisualPalette.CreateFrozenBrush(0x22, 0xD3, 0xEE, 235)
                : MediaBrushes.Transparent;
            CardBorder.BorderThickness = new Thickness(_dragMode ? 1.5 : 0);
            CardBorder.CornerRadius = new CornerRadius(_dragMode ? 8 : 0);
            CardBorder.Padding = new Thickness(4, 2, 4, 2);

            StatusTagBorder.Background = MediaBrushes.Transparent;
            StatusTagBorder.BorderBrush = MediaBrushes.Transparent;
            StatusTagBorder.BorderThickness = new Thickness(0);
            StatusTagBorder.Padding = new Thickness(0);
            StatusTagBorder.Visibility = urgency == UrgencyLevel.High ? Visibility.Visible : Visibility.Collapsed;
            HeaderDock.Margin = new Thickness(0, 0, 0, 3);
            return;
        }

        double effAlpha = _dragMode ? Math.Max(0.45, _backdropOpacity) : _backdropOpacity;
        byte bgAlpha = (byte)Math.Round(effAlpha * 255.0);
        byte borderAlpha = _dragMode
            ? (byte)245
            : effAlpha <= 0.03 ? (byte)0 : (byte)Math.Round(Math.Clamp(effAlpha * 235.0, 28, 220));
        byte outerAlpha = isCardStyle && effAlpha > 0.12 ? (byte)Math.Round(effAlpha * 90.0) : (byte)0;
        byte badgeAlpha = effAlpha <= 0.03 ? (byte)0 : (byte)Math.Round(Math.Clamp(effAlpha * 240.0, 30, 235));

        _cachedOuterBrush = outerAlpha == 0
            ? MediaBrushes.Transparent
            : HudVisualPalette.CreateFrozenBrush(0x02, 0x08, 0x10, outerAlpha);

        if (isCardStyle)
        {
            byte bgAlphaBottom = (byte)Math.Round(Math.Max(0, effAlpha - 0.04) * 255.0);
            var grad = new LinearGradientBrush(
                MediaColor.FromArgb(bgAlpha, 0x0A, 0x13, 0x1E),
                MediaColor.FromArgb(bgAlphaBottom, 0x07, 0x0D, 0x15),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1));
            grad.Freeze();
            _cachedCardBgBrush = grad;
        }
        else
        {
            _cachedCardBgBrush = bgAlpha == 0
                ? MediaBrushes.Transparent
                : HudVisualPalette.CreateFrozenBrush(0x07, 0x10, 0x1B, bgAlpha);
        }

        var bc = visual.BorderBaseColor;
        _cachedCardBorderBrush = borderAlpha == 0
            ? MediaBrushes.Transparent
            : HudVisualPalette.CreateFrozenBrush(bc.R, bc.G, bc.B, borderAlpha);

        var sBg = visual.BadgeBackgroundBrush.Color;
        var sBd = visual.BadgeBorderBrush.Color;
        _cachedStatusBgBrush = badgeAlpha == 0
            ? MediaBrushes.Transparent
            : HudVisualPalette.CreateFrozenBrush(sBg.R, sBg.G, sBg.B, badgeAlpha);
        _cachedStatusBorderBrush = borderAlpha == 0
            ? MediaBrushes.Transparent
            : HudVisualPalette.CreateFrozenBrush(sBd.R, sBd.G, sBd.B, borderAlpha);

        OuterShadowFrame.Background = _cachedOuterBrush;
        OuterShadowFrame.Padding = isCardStyle && outerAlpha > 0 ? new Thickness(2) : new Thickness(0);

        CardBorder.Background = _cachedCardBgBrush;
        CardBorder.BorderBrush = _cachedCardBorderBrush;
        CardBorder.BorderThickness = new Thickness(borderAlpha == 0 ? 0.0 : isCardStyle ? 1.35 : 1.0);
        CardBorder.CornerRadius = new CornerRadius(isCardStyle ? 11 : 10);
        CardBorder.Padding = isCardStyle ? new Thickness(13, 11, 13, 11) : new Thickness(11, 8, 11, 8);

        StatusTagBorder.Background = _cachedStatusBgBrush;
        StatusTagBorder.BorderBrush = _cachedStatusBorderBrush;
        StatusTagBorder.BorderThickness = new Thickness(borderAlpha == 0 ? 0.0 : 1.0);
        StatusTagBorder.Padding = borderAlpha == 0 ? new Thickness(0) : new Thickness(6, 1.5, 6, 1.5);
        StatusTagBorder.Visibility = Visibility.Visible;
        HeaderDock.Margin = new Thickness(0, 0, 0, isCardStyle ? 5 : 4);

        if (isCardStyle)
        {
            byte cardDetailAlpha = effAlpha <= 0.05 ? (byte)0 : (byte)Math.Round(Math.Clamp(effAlpha * 255.0, 28, 255));
            IconBadge.Background = cardDetailAlpha == 0
                ? MediaBrushes.Transparent
                : HudVisualPalette.CreateFrozenBrush(0x0A, 0x2B, 0x38, cardDetailAlpha);
            IconBadge.BorderBrush = _cachedCardBorderBrush;
            IconBadge.BorderThickness = new Thickness(borderAlpha == 0 ? 0.0 : 1.0);
            var chipBg = cardDetailAlpha == 0
                ? MediaBrushes.Transparent
                : HudVisualPalette.CreateFrozenBrush(0x09, 0x13, 0x1F, cardDetailAlpha);
            var chipBd = borderAlpha == 0
                ? MediaBrushes.Transparent
                : HudVisualPalette.CreateFrozenBrush(0x1B, 0x32, 0x47, borderAlpha);
            ChipPauseBorder.Background = chipBg;
            ChipPauseBorder.BorderBrush = chipBd;
            ChipAnalyzeBorder.Background = chipBg;
            ChipAnalyzeBorder.BorderBrush = chipBd;
        }
    }

    private void SyncRowsFromSnapshot(IReadOnlyList<OverlayFeedEntry> snapshot)
    {
        bool strongHalo = _style == UserConfig.OverlayStyleTransparentText || _backdropOpacity < 0.30;
        bool multiFeedActive = _maxMessages > 1 && (snapshot.Count > 1 || snapshot.Any(e => e.Kind == OverlayEntryKind.UserMessage));
        double msgFontSize = (_style == UserConfig.OverlayStyleCard ? 14.5 : 14.0) * _scale;
        double spkFontSize = 9.5 * _scale;

        bool sameShape = _rows.Count == snapshot.Count;
        if (sameShape)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (_rows[i].EntryId != snapshot[i].Id)
                {
                    sameShape = false;
                    break;
                }
            }
        }

        if (!sameShape)
        {
            _rows.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var entry = snapshot[i];
                var vm = new OverlayFeedRowViewModel { EntryId = entry.Id };
                ApplyEntryToRow(vm, entry, i, snapshot.Count, multiFeedActive, msgFontSize, spkFontSize, strongHalo);
                _rows.Add(vm);
            }
        }
        else
        {
            for (int i = 0; i < snapshot.Count; i++)
                ApplyEntryToRow(_rows[i], snapshot[i], i, snapshot.Count, multiFeedActive, msgFontSize, spkFontSize, strongHalo);
        }
    }

    private static void ApplyEntryToRow(
        OverlayFeedRowViewModel vm,
        OverlayFeedEntry entry,
        int index,
        int totalCount,
        bool showSpeakerTag,
        double msgFontSize,
        double spkFontSize,
        bool strongHalo)
    {
        var visual = HudVisualPalette.ResolveUrgency(entry.Urgency);
        bool isUser = entry.Kind == OverlayEntryKind.UserMessage;
        bool isStatus = entry.Kind == OverlayEntryKind.Status;
        bool isTactic = string.Equals(entry.Speaker, "TACTIC", StringComparison.OrdinalIgnoreCase);
        bool isBrief = string.Equals(entry.Speaker, "BRIEF", StringComparison.OrdinalIgnoreCase);

        vm.Kind = entry.Kind;
        vm.Urgency = entry.Urgency;
        vm.SpeakerLabel = isTactic
            ? "⚡ TACTIC"
            : isBrief
                ? "📋 BRIEF"
                : entry.Speaker.ToUpperInvariant();
        vm.Text = entry.Text;
        vm.SpeakerVisibility = (showSpeakerTag || isTactic || isBrief) ? Visibility.Visible : Visibility.Collapsed;
        vm.AccentBrush = isUser ? HudVisualPalette.HeaderCyan : visual.AccentBarBrush;
        vm.SpeakerBrush = isUser
            ? HudVisualPalette.SpeakerUserCyan
            : isTactic
                ? visual.TagForegroundBrush
                : isStatus
                    ? HudVisualPalette.SpeakerStatusCyan
                    : visual.TagForegroundBrush;
        vm.MessageBrush = isUser || isBrief
            ? HudVisualPalette.TextUserCyan
            : isStatus
                ? HudVisualPalette.TextStatusMuted
                : HudVisualPalette.TextWhitePrimary;
        vm.SpeakerFontSize = spkFontSize;
        vm.MessageFontSize = isUser ? Math.Max(11.5, msgFontSize - 1.0) : msgFontSize;
        vm.StrongHalo = strongHalo;

        int distFromNewest = Math.Max(0, (totalCount - 1) - index);
        vm.RowOpacity = distFromNewest switch
        {
            0 => 1.0,
            1 => 0.84,
            _ => 0.72
        };
        vm.RowMargin = index == totalCount - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 7);
    }

    private void FadeOutAndHide()
    {
        if (_closed || !IsVisible || _quickChatOpen) return;
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150));
        Timeline.SetDesiredFrameRate(anim, 24);
        anim.Completed += (_, _) =>
        {
            if (!_closed && !_quickChatOpen && _feedBuffer.IsEmpty)
            {
                _topmostGuardTimer.Stop();
                BeginAnimation(OpacityProperty, null);
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void ShutdownCleanly()
    {
        _closed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _pruneTimer.Stop();
        _topmostGuardTimer.Stop();
        try { Close(); } catch { }
    }

    private void ReassertTopmostAndActiveMonitor(bool forceReposition = false)
    {
        if (_closed) return;

        bool isCustomPos = string.Equals(_position, "custom", StringComparison.OrdinalIgnoreCase) &&
                           _customLeft.HasValue && _customTop.HasValue;
        bool monitorChanged = false;
        if (_followActiveMonitor && !isCustomPos && !_dragMode)
        {
            IntPtr extHwnd = Win32WindowDetector.GetExternalForegroundWindowHandle();
            if (extHwnd != IntPtr.Zero)
            {
                int? fgMon = Win32DisplayHelper.TryGetMonitorIndexForWindow(extHwnd);
                if (fgMon.HasValue && fgMon.Value != _autoMonitorIndex)
                {
                    _autoMonitorIndex = fgMon.Value;
                    monitorChanged = true;
                }
            }
        }

        if (!_dragMode && (forceReposition || monitorChanged))
            PositionOnScreen(force: true);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && IsVisible)
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    private void PositionOnScreen(bool force = false)
    {
        if (_dragMode)
            return;

        if (string.Equals(_position, "custom", StringComparison.OrdinalIgnoreCase) &&
            _customLeft is double customLeft && _customTop is double customTop)
        {
            double customH = Math.Max(52, ActualHeight);
            var (clampedL, clampedT) = ClampOverlayDipCoords(customLeft, customTop, Width, customH);
            string customKey = $"custom:{clampedL:F0},{clampedT:F0}:{Width:F0}:{customH:F0}";
            if (!force && customKey == _lastPosKey)
                return;
            _lastPosKey = customKey;
            _lastActualH = customH;
            Left = clampedL;
            Top = clampedT;
            var cHwnd = new WindowInteropHelper(this).Handle;
            if (cHwnd != IntPtr.Zero && IsVisible)
                SetWindowPos(cHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            return;
        }

        if (_screensDirty || _cachedWorkingAreas.Length == 0)
        {
            _cachedWorkingAreas = Win32DisplayHelper.GetAllWorkingAreas();
            _screensDirty = false;
        }

        int effectiveMonitor = _followActiveMonitor && _autoMonitorIndex is int autoIdx
            ? autoIdx
            : _monitorIndex;
        int idx = Math.Clamp(effectiveMonitor - 1, 0, Math.Max(0, _cachedWorkingAreas.Length - 1));
        var waPx = _cachedWorkingAreas.Length > 0 ? _cachedWorkingAreas[idx] : new MonitorBoundsRect(0, 0, 1920, 1080);

        double h = Math.Max(52, ActualHeight);
        string posKey = $"{effectiveMonitor}:{_position}:{Width:F0}:{h:F0}:{waPx.Left},{waPx.Top},{waPx.Width},{waPx.Height}";
        if (!force && posKey == _lastPosKey)
            return;
        _lastPosKey = posKey;
        _lastActualH = h;

        var (scaleX, scaleY) = GetTargetMonitorScale(waPx);
        double targetDpiScaleX = scaleX > 0 ? (1.0 / scaleX) : 1.0;
        double targetDpiScaleY = scaleY > 0 ? (1.0 / scaleY) : 1.0;

        int padPx = (int)Math.Round(20 * targetDpiScaleX);
        int widthPx = (int)Math.Round(Width * targetDpiScaleX);
        int heightPx = (int)Math.Round(h * targetDpiScaleY);

        int pxLeft = _position.Contains("center")
            ? waPx.Left + (waPx.Width - widthPx) / 2
            : _position.Contains("left")
                ? waPx.Left + padPx
                : waPx.Left + waPx.Width - widthPx - padPx;

        int pxTop = _position.Contains("bottom")
            ? waPx.Top + waPx.Height - heightPx - padPx
            : waPx.Top + padPx;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && IsVisible)
        {
            SetWindowPos(hwnd, HwndTopmost, pxLeft, pxTop, 0, 0, SwpNoActivate | SwpNoSize);
        }
        else
        {
            Left = pxLeft * scaleX;
            Top = pxTop * scaleY;
        }
    }

    private (double ScaleX, double ScaleY) GetTargetMonitorScale(MonitorBoundsRect waPx)
    {
        try
        {
            var pt = new POINT { X = waPx.Left + Math.Max(1, waPx.Width / 2), Y = waPx.Top + Math.Max(1, waPx.Height / 2) };
            var hMon = MonitorFromPoint(pt, MonitorDefaultToNearest);
            if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, MdtEffectiveDpi, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return (96.0 / dpiX, 96.0 / dpiY);
        }
        catch { }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
            return (source.CompositionTarget.TransformFromDevice.M11, source.CompositionTarget.TransformFromDevice.M22);
        return (1.0, 1.0);
    }
}
