using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly DispatcherTimer _pruneTimer;
    private readonly OverlayFeedBuffer _feedBuffer = new();
    private readonly ObservableCollection<OverlayFeedRowViewModel> _rows = [];

    private MonitorBoundsRect[] _cachedWorkingAreas = [];
    private bool _screensDirty = true;
    private string _position = "top-right";
    private int _monitorIndex = 1;
    private double _backdropOpacity = 0.55;
    private double _scale = 1.0;
    private string _style = UserConfig.OverlayStyleDiscordPill;
    private int _maxMessages = 3;
    private UrgencyLevel _activeHeaderUrgency = UrgencyLevel.Low;
    private string _activeHeaderTitle = "HUDMIND";
    private bool _closed;
    private double _lastActualH;
    private string _lastPosKey = string.Empty;

    private string _cachedThemeKey = string.Empty;
    private MediaBrush? _cachedOuterBrush;
    private MediaBrush? _cachedCardBgBrush;
    private MediaBrush? _cachedCardBorderBrush;
    private MediaBrush? _cachedStatusBgBrush;
    private MediaBrush? _cachedStatusBorderBrush;

    public OverlayWindow()
    {
        InitializeComponent();
        FeedItemsHost.ItemsSource = _rows;
        OverlayWave.SetBarHeightScale(18);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pruneTimer.Tick += (_, _) => OnPruneTimerTick();

        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_closed && Math.Abs(ActualHeight - _lastActualH) > 1.0)
                PositionOnScreen(force: true);
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
        var hwnd = new WindowInteropHelper(this).Handle;
        if (IntPtr.Size == 8)
        {
            nint exStyle = GetWindowLongPtr64(hwnd, GwlExStyle);
            SetWindowLongPtr64(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }
        else
        {
            int exStyle = GetWindowLong32(hwnd, GwlExStyle);
            SetWindowLong32(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }
    }

    public void SetHotkeyHints(string pauseKey, string analyzeKey)
    {
        if (_closed) return;
        var p = string.IsNullOrWhiteSpace(pauseKey) ? "F9" : pauseKey.Trim().ToUpperInvariant();
        var a = string.IsNullOrWhiteSpace(analyzeKey) ? "F10" : analyzeKey.Trim().ToUpperInvariant();
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            TxtPauseChip.Text = $"{p} PAUSA";
            TxtAnalyzeChip.Text = $"{a} IA";
        });
    }

    public void Configure(
        string position,
        int width,
        double opacity,
        int monitorIndex = 1,
        double scale = 1.0,
        bool compact = false,
        string? style = null,
        int maxMessages = 3)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _position = string.IsNullOrWhiteSpace(position) ? "top-right" : position.ToLowerInvariant();
            _monitorIndex = Math.Max(1, monitorIndex);
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
                if (_rows.Count == 0)
                {
                    _pruneTimer.Stop();
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
            _feedBuffer.Clear();
            _rows.Clear();
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

            bool wasVisible = IsVisible;
            if (!wasVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Show();
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            }

            UpdateLayout();
            PositionOnScreen(force: !wasVisible);

            if (!wasVisible)
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

            ScheduleNextPrune(nowUtc);
        });
    }

    private void OnPruneTimerTick()
    {
        if (_closed)
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
        StatusTagText.Text = visual.TagText;

        if (isCardStyle)
            OverlayWave.SetBarHeightScale(Math.Round(18.0 * _scale));

        int alphaPct = (int)Math.Round(_backdropOpacity * 100);
        string themeKey = $"{_style}:{alphaPct}:{urgency}";
        if (themeKey == _cachedThemeKey)
            return;
        _cachedThemeKey = themeKey;

        if (isTransparentStyle)
        {
            OuterShadowFrame.Background = MediaBrushes.Transparent;
            OuterShadowFrame.Padding = new Thickness(0);
            CardBorder.Background = MediaBrushes.Transparent;
            CardBorder.BorderBrush = MediaBrushes.Transparent;
            CardBorder.BorderThickness = new Thickness(0);
            CardBorder.Padding = new Thickness(4, 2, 4, 2);

            StatusTagBorder.Background = MediaBrushes.Transparent;
            StatusTagBorder.BorderBrush = MediaBrushes.Transparent;
            StatusTagBorder.BorderThickness = new Thickness(0);
            StatusTagBorder.Padding = new Thickness(0);
            StatusTagBorder.Visibility = urgency == UrgencyLevel.High ? Visibility.Visible : Visibility.Collapsed;
            HeaderDock.Margin = new Thickness(0, 0, 0, 3);
            return;
        }

        double effAlpha = _backdropOpacity;
        byte bgAlpha = (byte)Math.Round(effAlpha * 255.0);
        byte borderAlpha = effAlpha <= 0.03 ? (byte)0 : (byte)Math.Round(Math.Clamp(effAlpha * 235.0, 28, 220));
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

        vm.Kind = entry.Kind;
        vm.Urgency = entry.Urgency;
        vm.SpeakerLabel = entry.Speaker.ToUpperInvariant();
        vm.Text = entry.Text;
        vm.SpeakerVisibility = showSpeakerTag ? Visibility.Visible : Visibility.Collapsed;
        vm.AccentBrush = isUser ? HudVisualPalette.HeaderCyan : visual.AccentBarBrush;
        vm.SpeakerBrush = isUser
            ? HudVisualPalette.SpeakerUserCyan
            : isStatus
                ? HudVisualPalette.SpeakerStatusCyan
                : visual.TagForegroundBrush;
        vm.MessageBrush = isUser
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
        if (_closed || !IsVisible) return;
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150));
        Timeline.SetDesiredFrameRate(anim, 24);
        anim.Completed += (_, _) =>
        {
            if (!_closed && _feedBuffer.IsEmpty)
            {
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
        try { Close(); } catch { }
    }

    private void PositionOnScreen(bool force = false)
    {
        if (_screensDirty || _cachedWorkingAreas.Length == 0)
        {
            _cachedWorkingAreas = Win32DisplayHelper.GetAllWorkingAreas();
            _screensDirty = false;
        }

        int idx = Math.Clamp(_monitorIndex - 1, 0, Math.Max(0, _cachedWorkingAreas.Length - 1));
        var waPx = _cachedWorkingAreas.Length > 0 ? _cachedWorkingAreas[idx] : new MonitorBoundsRect(0, 0, 1920, 1080);

        double h = Math.Max(52, ActualHeight);
        string posKey = $"{_monitorIndex}:{_position}:{Width:F0}:{h:F0}:{waPx.Left},{waPx.Top},{waPx.Width},{waPx.Height}";
        if (!force && posKey == _lastPosKey)
            return;
        _lastPosKey = posKey;
        _lastActualH = h;

        // Query target monitor's exact DPI via shcore.dll and place using physical pixels via SetWindowPos
        // so mixed-DPI multi-monitor setups position accurately without WPF cross-monitor DPI distortion!
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
