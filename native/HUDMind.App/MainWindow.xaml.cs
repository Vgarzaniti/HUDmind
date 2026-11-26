using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;
using HUDMind.Core.Updates;
using HUDMind.Windows;
using MediaColor = System.Windows.Media.Color;

namespace HUDMind.App;

public sealed class AccountRowView
{
    public required ProviderAccount Source { get; init; }
    public int Priority => Source.Priority;
    public string Label => Source.Label;
    public string Provider => Source.Provider;
    public string ProviderDisplay => CoachFeedFormatter.FriendlyShortProvider(Source.Provider);
    public string Model => Source.Model;
    public string VisionDisplay => Source.SupportsVisionOverride.HasValue
        ? (Source.SupportsVisionOverride.Value ? "Visión*" : "OCR/Logs*")
        : (Source.SupportsVision ? "Visión" : "OCR/Logs");
    public string MaskedKeyDisplay => Source.MaskedKey();
    public string EnabledDisplay => Source.Enabled ? "Sí" : "No";
}

public partial class MainWindow : Window
{
    private const int MaxLogEntries = 180;
    private const int MaxFeedEntries = 120;

    private readonly ConfigRepository _repo = new();
    private readonly CoachEngineCoordinator _coordinator = new();
    private readonly object _memoryIoLock = new();
    private readonly object _profilesLock = new();
    private readonly object _accountsLock = new();
    private UserConfig _config;
    private List<ProviderAccount> _accounts;
    private List<GameProfile> _profiles;

    private readonly ObservableCollection<string> _logEntries = [];
    private readonly ObservableCollection<CoachFeedItem> _feedEntries = [];
    private readonly OverlayWindow _overlay = new();
    private readonly HybridVoiceService _sharedVoice;
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly HudNavigationMediator _navMediator;
    private Win32TrayIcon? _trayIcon;
    private DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _updateCts;
    private AppUpdateInfo? _availableUpdate;
    private string? _downloadedUpdatePath;
    private string? _dismissedUpdateTag;
    private bool _checkingUpdates;
    private bool _downloadingUpdate;

    private volatile bool _shutdownComplete;
    private volatile bool _closingInProgress;
    private bool _syncingUi;
    private bool _chatSending;
    private bool _rulesDirty;
    private bool _onboardingDismissed;
    private string? _lastRulesGameId;
    private string? _editingAccountId;
    private readonly bool _smokeMode;

    public MainWindow(bool smokeMode = false)
    {
        _smokeMode = smokeMode;
        if (_smokeMode)
        {
            ShowInTaskbar = false;
            ShowActivated = false;
            Left = -32000;
            Top = -32000;
            Opacity = 0;
        }

        InitializeComponent();
        _navMediator = new HudNavigationMediator(MainTabs, [NavCoach, NavAccounts, NavGames, NavSettings]);
        SettingsVoiceWave.SetBarHeightScale(26);
        LstCoachFeed.ItemsSource = _feedEntries;
        LstLiveLogs.ItemsSource = _logEntries;
        _feedEntries.Add(new CoachFeedItem
        {
            Timestamp = DateTime.Now.ToString("HH:mm"),
            Author = "HUDMind",
            Message = "Listo para acompañarte. Elegí tu juego (o dejalo en Automático) y tocá Activar Coach.",
            AccentHex = "#10B981",
            CardBackgroundHex = "#081C19",
            AuthorForegroundHex = "#6EE7B7"
        });

        _sharedVoice = new HybridVoiceService(_repo.WorkspaceRoot);
        _config = _repo.LoadUserConfig();
        _sharedVoice.SetSpeechRate(_config.TtsRate);
        _sharedVoice.SetOutputDevice(_config.AudioOutputDeviceName);
        _accounts = _repo.LoadAccounts();
        _repo.EnsureDefaultProfilesSeeded();
        _profiles = _repo.LoadProfiles();
        _profiles.Insert(0, ConfigRepository.CreateUniversalProfile(_repo.ProfilesDir));

        BindInitialUi();
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _trayIcon = new Win32TrayIcon(
            onRestore: () => Dispatcher.BeginInvoke(RestoreFromTray),
            onTogglePause: () => _coordinator.TogglePause(),
            onExit: () => Dispatcher.BeginInvoke(Close),
            isPaused: () => _coordinator.IsPaused,
            isRunning: () => _coordinator.IsRunning);
        _trayIcon.Attach(this);
        if (_smokeMode)
        {
            _ = Dispatcher.BeginInvoke(Close);
            return;
        }
        if (_config.MinimizeToTray && WindowState == WindowState.Minimized)
        {
            Hide();
            _trayIcon.Show();
        }
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(60) };
        _updateTimer.Tick += (_, _) =>
        {
            if (_config.CheckUpdatesOnStartup && !_coordinator.IsRunning && !_checkingUpdates && !_downloadingUpdate)
                _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        if (_config.CheckUpdatesOnStartup)
            _ = CheckForUpdatesAsync(manual: false);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_config.MinimizeToTray && WindowState == WindowState.Minimized && !_shutdownComplete)
        {
            Hide();
            _trayIcon?.Show(HeaderStatusText.Text);
        }
    }

    private void RestoreFromTray()
    {
        if (_shutdownComplete) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _trayIcon?.Hide();
    }

    private void RefreshProfilePickers(string? selectMainId = null, string? selectRulesId = null)
    {
        var mainOptions = GetProfilesSnapshot()
            .Select(p => new GameProfileOption(
                p.Manifest.GameId,
                p.Manifest.GameId == "universal"
                    ? "🌐 Cualquier Juego (Automático)"
                    : p.Manifest.DisplayName))
            .ToList();

        CmbGameProfile.ItemsSource = mainOptions;
        if (!string.IsNullOrWhiteSpace(selectMainId) && mainOptions.Any(o => o.GameId == selectMainId))
            CmbGameProfile.SelectedValue = selectMainId;
        else if (mainOptions.Count > 0)
            CmbGameProfile.SelectedIndex = 0;

        var rulesOptions = mainOptions.ToList();
        CmbRulesGame.ItemsSource = rulesOptions;
        if (!string.IsNullOrWhiteSpace(selectRulesId) && rulesOptions.Any(o => o.GameId == selectRulesId))
            CmbRulesGame.SelectedValue = selectRulesId;
        else if (rulesOptions.Count > 0)
            CmbRulesGame.SelectedIndex = 0;
    }

    private void BindInitialUi()
    {
        RefreshProfilePickers();

        CmbNewProvider.ItemsSource = ProviderRegistry.AllPresets
            .Select(p => new ProviderOption(p.Id, CoachFeedFormatter.FriendlyProviderName(p.Id)))
            .ToList();
        if (CmbNewProvider.Items.Count > 0) CmbNewProvider.SelectedIndex = 0;

        RefreshMonitorsList();
        RefreshAudioDevicesList();

        _syncingUi = true;
        try
        {
            SelectByTag(CmbCoachMode, _config.CoachMode);
            SelectByTag(CmbSkillTier, _config.PlayerSkillTier);
            TxtRankLabel.Text = _config.PlayerRankLabel;
            SelectByTag(CmbCoachStyle, _config.CoachStyle);
            SelectByTag(CmbLiveAutoRate, NormalizeLiveAutoRateBucket(_config.LiveAutoCoachSeconds));
            SelectByTag(CmbVoiceGender, _config.VoiceGender);
            SelectByTag(CmbQuickGender, _config.VoiceGender);
            SelectByTag(CmbVoiceStyle, _config.VoiceStyle);
            SelectByTag(CmbVoiceRate, NormalizeVoiceRateBucket(_config.TtsRate));
            SelectByTag(CmbEngineType, _config.UseLiveConversational ? "live" : "classic");
            SelectByTag(CmbCommMode, _config.CommunicationMode);
            SelectByTag(CmbCommModeSettings, _config.CommunicationMode);
            SelectByTag(CmbMicInputMode, _config.IsPushToTalkMode ? "push_to_talk" : "open_mic");
            TxtPttKey.Text = string.IsNullOrWhiteSpace(_config.PushToTalkKey) ? "V" : _config.PushToTalkKey;
            SelectByTag(CmbMicDevice, _config.AudioInputDeviceName);
            SelectByTag(CmbSpeakerDevice, _config.AudioOutputDeviceName);
            SelectByTag(CmbLanguage, _config.VoiceLanguage);
            SelectByTag(CmbEfficiency, _config.TokenEfficiencyMode);
            string memBucket = _config.MemoryMatchWindow switch { <= 7 => "5", <= 15 => "10", _ => "20" };
            SelectByTag(CmbMemoryWindow, memBucket);
            SelectByTag(CmbOverlayStyle, _config.ResolveOverlayStyle());
            SelectByTag(CmbOverlayFeedLines, _config.ResolveOverlayMaxMessages() == 1 ? "1" : "3");
            SelectByTag(CmbOverlayPos, _config.OverlayPosition);
            SldOverlayScale.Value = Math.Clamp(_config.OverlayScale, 0.75, 1.45);
            SldOverlayOpacity.Value = Math.Clamp(_config.OverlayOpacity, 0.0, 0.95);
            UpdateOverlaySliderLabels();
            SldVolume.Value = _config.TtsVolume;
            LblVolumePct.Text = $"{(int)Math.Round(_config.TtsVolume * 100)}%";
        }
        finally
        {
            _syncingUi = false;
        }

        ChkEnableOverlay.IsChecked = _config.EnableOverlay;
        ChkEnableHotkeys.IsChecked = _config.EnableHotkeys;
        ChkMinimizeToTray.IsChecked = _config.MinimizeToTray;
        ChkEnableAudioCues.IsChecked = _config.EnableAudioCues;
        ChkCheckUpdatesOnStartup.IsChecked = _config.CheckUpdatesOnStartup;
        TxtCoachSoul.Text = _repo.LoadCoachSoulMarkdown();
        TxtPlayerProfile.Text = _repo.LoadPlayerProfileMarkdown();
        TxtPauseKey.Text = _config.HotkeyTogglePause;
        TxtAnalyzeKey.Text = _config.HotkeyAnalyzeNow;
        TxtMicKey.Text = _config.HotkeyToggleMic;
        RefreshHotkeyButtonLabels();

        var (curVer, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
        BtnVersionCheck.Content = $"v{GitHubReleaseUpdateService.FormatVersion(curVer)}";

        ConfigureOverlayFromCurrentConfig();
        RefreshAccountsList();
        RefreshOnboardingBanner();
        RefreshRollingMemoryUi();
        UpdateLatencyCardHeaders(isLive: false);
        ResetStatsCards();

        if (!string.IsNullOrWhiteSpace(_repo.LastLoadWarning))
            AppendLog($"⚠ AVISO: {_repo.LastLoadWarning}");

        AppendLog($"HUDMind Native (.NET 9 · 0% GPU) listo | Workspace: {_repo.WorkspaceRoot}");
        AppendLog($"Perfiles cargados: {_profiles.Count} | Cuentas IA listas: {_accounts.Count(a => a.IsReady())}");
    }

    private void RefreshMonitorsList()
    {
        int screenCount = Math.Max(1, Win32DisplayHelper.GetAllWorkingAreas().Length);
        CmbMonitor.ItemsSource = Enumerable.Range(1, screenCount)
            .Select(i => new MonitorOption(i, i == 1 ? "Monitor 1 (Principal)" : $"Monitor {i}"))
            .ToList();
        CmbMonitor.SelectedValue = Math.Clamp(_config.CaptureMonitor, 1, screenCount);
    }

    private void RefreshAudioDevicesList()
    {
        CmbMicDevice.Items.Clear();
        CmbMicDevice.Items.Add(new ComboBoxItem { Content = "(Predeterminado de Windows)", Tag = string.Empty });
        foreach (var dev in Win32AudioDevices.GetInputDeviceNames())
            CmbMicDevice.Items.Add(new ComboBoxItem { Content = dev, Tag = dev });

        CmbSpeakerDevice.Items.Clear();
        CmbSpeakerDevice.Items.Add(new ComboBoxItem { Content = "(Predeterminado de Windows)", Tag = string.Empty });
        foreach (var dev in Win32AudioDevices.GetOutputDeviceNames())
            CmbSpeakerDevice.Items.Add(new ComboBoxItem { Content = dev, Tag = dev });
    }

    private void ConfigureOverlayFromCurrentConfig()
    {
        string style = _config.ResolveOverlayStyle();
        int maxMessages = _config.ResolveOverlayMaxMessages();
        _overlay.SetHotkeyHints(_config.HotkeyTogglePause, _config.HotkeyAnalyzeNow);
        _overlay.Configure(
            _config.OverlayPosition,
            _config.OverlayWidth,
            _config.OverlayOpacity,
            _config.CaptureMonitor,
            _config.OverlayScale,
            compact: style != UserConfig.OverlayStyleCard,
            style: style,
            maxMessages: maxMessages);
    }

    private List<GameProfile> GetProfilesSnapshot()
    {
        lock (_profilesLock)
            return [.. _profiles];
    }

    private GameProfile? FindProfileByIdLocked(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        lock (_profilesLock)
            return _profiles.FirstOrDefault(p => string.Equals(p.Manifest.GameId, gameId, StringComparison.OrdinalIgnoreCase));
    }

    private List<ProviderAccount> GetAccountsSnapshot()
    {
        lock (_accountsLock)
            return [.. _accounts];
    }

    private string[] GetAccountApiKeysSnapshot()
    {
        lock (_accountsLock)
            return [.. _accounts.Select(a => a.ApiKey)];
    }

    private void RefreshOnboardingBanner()
    {
        if (OnboardingBanner is null) return;
        bool hasUserApiKeyAccount;
        lock (_accountsLock)
        {
            hasUserApiKeyAccount = _accounts.Any(a =>
                a.Enabled && !a.IsKeylessAllowed && !string.IsNullOrWhiteSpace(a.ApiKey));
        }
        OnboardingBanner.Visibility = (!hasUserApiKeyAccount && !_onboardingDismissed) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshMatchReviewButtonState()
    {
        if (BtnMatchReview is null) return;
        bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        bool hasReady;
        lock (_accountsLock)
            hasReady = _accounts.Any(a => a.IsReady());
        BtnMatchReview.IsEnabled = _coordinator.HasMatchHistory(isEn) && hasReady;
    }

    private void RefreshHotkeyButtonLabels()
    {
        BtnAnalyzeNow.Content = $"🎯 Analizar ({_config.HotkeyAnalyzeNow})";
        BtnPause.Content = _coordinator.IsPaused
            ? $"▶ Reanudar ({_config.HotkeyTogglePause})"
            : $"⏸ Pausar ({_config.HotkeyTogglePause})";
        _overlay.SetHotkeyHints(_config.HotkeyTogglePause, _config.HotkeyAnalyzeNow);
    }

    private void UpdateLatencyCardHeaders(bool isLive) =>
        ActiveBanner.UpdateHeaders(isLive);

    private string BuildActiveBannerSubtitle()
    {
        var engine = _coordinator.IsRunning
            ? (_coordinator.SupportsLiveMic ? "Live WebSocket Audio+Video" : "Turnos Multi-IA")
            : (_config.UseLiveConversational ? "Live listo · Auto-Detectar" : "Turnos listo · Auto-Detectar");
        var style = _config.CoachStyle switch
        {
            "honest" => "Sincero",
            "tactical" => "Táctico",
            "strict" => "Torneo",
            _ => "Amigable"
        };
        return $"{engine} · Estilo {style} · 0% GPU pasivo";
    }

    private (string Label, bool Highlighted) ResolveContextBadge()
    {
        bool hasMemoryOrSoul = !string.IsNullOrWhiteSpace(_config.PreviousMatchFocus) ||
                               !string.IsNullOrWhiteSpace(_config.CoachSoulSummary);
        if (_coordinator.IsRunning)
            return hasMemoryOrSoul ? ("Memoria & Contexto: ACTIVO", true) : ("Observación en vivo: ACTIVA", true);
        return hasMemoryOrSoul ? ("Memoria .md: CARGADA", false) : ("Memoria .md: LISTO", false);
    }

    private void ApplyRunStateVisual(HudRunState state)
    {
        var v = HudVisualPalette.ResolveRunState(state);
        StatusDot.Fill = v.DotBrush;
        StatusPillBorder.Background = v.PillBackgroundBrush;
        StatusPillBorder.BorderBrush = v.PillBorderBrush;
        HeaderStatusText.Foreground = v.TextBrush;
        BtnStartStop.Background = v.PrimaryButtonBackgroundBrush;
        BtnStartStop.BorderBrush = v.PrimaryButtonBorderBrush;
        BtnStartStop.Content = v.PrimaryButtonLabel;
    }

    private void ResetStatsCards()
    {
        ActiveBanner.Reset(_config.IsTextOnlyMode, BuildActiveBannerSubtitle());
        var (ctxLabel, ctxActive) = ResolveContextBadge();
        ActiveBanner.SetContextStatus(ctxLabel, ctxActive);
        ApplyRunStateVisual(HudRunState.Idle);
    }

    private void SetRunningControlLocks(bool running)
    {
        CmbEngineType.IsEnabled = !running;
        CmbRunMode.IsEnabled = !running;
        CmbGameProfile.IsEnabled = !running;
        BtnAnalyzeNow.IsEnabled = running;
        BtnPause.IsEnabled = running;
        TxtChatInput.IsEnabled = running;
        BtnSendChat.IsEnabled = running;
    }

    private void RefreshAccountsList()
    {
        List<AccountRowView> rows;
        lock (_accountsLock)
        {
            for (int i = 0; i < _accounts.Count; i++)
                _accounts[i].Priority = i + 1;
            rows = _accounts.Select(a => new AccountRowView { Source = a }).ToList();
        }
        LstAccounts.ItemsSource = rows;
        RefreshOnboardingBanner();
        RefreshMatchReviewButtonState();
    }

    private void AppendLog(string message)
    {
        if (_shutdownComplete) return;
        var safeMsg = SecretRedactor.Redact(message, GetAccountApiKeysSnapshot());
        var now = DateTime.Now;
        var line = $"[{now:HH:mm:ss}] {safeMsg}";
        var feedItem = CoachFeedFormatter.TryFormatFeedItem(safeMsg, now.ToString("HH:mm"));
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete) return;
            _logEntries.Add(line);
            while (_logEntries.Count > MaxLogEntries)
                _logEntries.RemoveAt(0);
            if (_logEntries.Count > 0 && WindowState != WindowState.Minimized)
                LstLiveLogs.ScrollIntoView(_logEntries[^1]);

            if (feedItem is not null)
            {
                _feedEntries.Add(feedItem);
                while (_feedEntries.Count > MaxFeedEntries)
                    _feedEntries.RemoveAt(0);
                if (_feedEntries.Count > 0 && WindowState != WindowState.Minimized)
                    LstCoachFeed.ScrollIntoView(_feedEntries[^1]);
            }
        });
    }

    private void UpdateStatsUi(CoachRuntimeStats stats)
    {
        if (_shutdownComplete) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete || !_coordinator.IsRunning) return;
            var voiceStatus = _config.IsTextOnlyMode
                ? "Silencio"
                : (_coordinator.SupportsLiveMic
                    ? (_config.IsMicActive
                        ? (_config.IsPushToTalkMode ? $"PTT ({_config.PushToTalkKey})" : "Mic activo")
                        : "Solo voz coach")
                    : "Voz activa");
            var (ctxLabel, ctxActive) = ResolveContextBadge();
            ActiveBanner.UpdateMetrics(
                stats.ActiveGame,
                BuildActiveBannerSubtitle(),
                stats.LastAiMs,
                voiceStatus,
                stats.Interventions,
                ctxLabel,
                ctxActive);
            var provLabel = string.IsNullOrWhiteSpace(stats.ActiveProvider) || stats.ActiveProvider == "-"
                ? "IA"
                : (stats.ActiveProvider.Contains(' ') ? stats.ActiveProvider : CoachFeedFormatter.FriendlyShortProvider(stats.ActiveProvider));
            HeaderStatusText.Text = stats.IsPaused
                ? $"Pausado · {provLabel}"
                : $"En partida · {provLabel}";
            ApplyRunStateVisual(stats.IsPaused ? HudRunState.Paused : HudRunState.Active);
            PauseBadge.Visibility = stats.IsPaused ? Visibility.Visible : Visibility.Collapsed;
            _trayIcon?.UpdateTooltip($"HUDMind · {HeaderStatusText.Text}");
            RefreshMatchReviewButtonState();
            RefreshHotkeyButtonLabels();
        });
    }

    private void OnPauseChanged(bool paused)
    {
        if (_shutdownComplete) return;
        if (_config.EnableAudioCues && !_config.IsTextOnlyMode)
            Win32AudioCue.Play(paused ? AudioCueKind.SoftOff : AudioCueKind.SoftOn);
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete || !_coordinator.IsRunning) return;
            PauseBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            ApplyRunStateVisual(paused ? HudRunState.Paused : HudRunState.Active);
            RefreshHotkeyButtonLabels();
        });
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        if (_closingInProgress)
            return;
        _closingInProgress = true;

        try
        {
            _updateTimer?.Stop();
            _updateTimer = null;
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = null;
            await _coordinator.DisposeAsync();
            _sharedVoice.Dispose();
            _trayIcon?.Dispose();
            _overlay.ShutdownCleanly();
        }
        finally
        {
            _shutdownComplete = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private async void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_closingInProgress || _shutdownComplete)
            return;
        BtnStartStop.IsEnabled = false;
        try
        {
            if (_coordinator.IsRunning)
            {
                await _coordinator.StopAsync();
                if (_closingInProgress || _shutdownComplete)
                    return;
                ApplyStoppedUiState();
                return;
            }

            ApplyConfigFromUi();
            _repo.SaveUserConfig(_config);
            ConfigureOverlayFromCurrentConfig();

            bool testMode = GetSelectedTag(CmbRunMode) == "test";
            string? forcedId = CmbGameProfile.SelectedValue?.ToString();
            bool wantLive = GetSelectedTag(CmbEngineType) == "live";
            var profilesSnapshot = GetProfilesSnapshot();
            var accountsSnapshot = GetAccountsSnapshot();
            var hintProf = forcedId is not null
                ? profilesSnapshot.FirstOrDefault(p => p.Manifest.GameId == forcedId)
                : null;
            SyncAssembledContext(forcedId ?? "universal", hintProf, CoachContextDepth.RealtimePlay);
            if (!string.IsNullOrWhiteSpace(_config.PreviousMatchFocus))
                AppendLog($"🧠 Memoria de partidas activa: {_config.PreviousMatchFocus}");

            try
            {
                var result = await _coordinator.StartAsync(
                    _config,
                    accountsSnapshot,
                    profilesSnapshot,
                    _sharedVoice,
                    _overlay,
                    testMode,
                    forcedId,
                    wantLive,
                    AppendLog,
                    UpdateStatsUi,
                    OnPauseChanged,
                    ToggleMicViaHotkey,
                    HandleMatchCompleted,
                    ResolveContextForProfileSafe);

                if (_closingInProgress || _shutdownComplete || !result.Started)
                    return;

                if (result.FallbackToClassic)
                {
                    _syncingUi = true;
                    try { SelectByTag(CmbEngineType, "classic"); }
                    finally { _syncingUi = false; }
                }

                UpdateLatencyCardHeaders(isLive: _coordinator.SupportsLiveMic);
                TxtHeaderBadge.Text = result.Badge;
                HeaderStatusText.Text = result.Status;
                ApplyRunStateVisual(HudRunState.Active);
                var (ctxLabel, ctxActive) = ResolveContextBadge();
                ActiveBanner.SetContextStatus(ctxLabel, ctxActive);
                SetRunningControlLocks(true);
                PauseBadge.Visibility = Visibility.Collapsed;
                RefreshHotkeyButtonLabels();
                _trayIcon?.UpdateTooltip($"HUDMind · {HeaderStatusText.Text}");
            }
            catch (Exception ex)
            {
                if (_closingInProgress || _shutdownComplete)
                    return;
                AppendLog($"Error iniciando motor: {SecretRedactor.Redact(ex.Message, GetAccountApiKeysSnapshot())}");
                await _coordinator.StopAsync();
                if (_closingInProgress || _shutdownComplete)
                    return;
                ApplyStoppedUiState();
            }
        }
        finally
        {
            if (!_closingInProgress && !_shutdownComplete)
                BtnStartStop.IsEnabled = true;
        }
    }

    private AssembledCoachContext ResolveContextForProfileSafe(GameProfile profile)
    {
        lock (_memoryIoLock)
            return _repo.LoadAssembledCoachContext(profile.Manifest.GameId, _config, profile, CoachContextDepth.RealtimePlay);
    }

    private void SyncAssembledContext(string? gameId, GameProfile? profile, CoachContextDepth depth)
    {
        AssembledCoachContext assembled;
        lock (_memoryIoLock)
            assembled = _repo.LoadAssembledCoachContext(gameId, _config, profile, depth);
        CoachContextAssembler.ApplyToConfig(_config, assembled);
    }

    private void HandleMatchCompleted(MatchSessionSnapshot snap)
    {
        try
        {
            var gameId = string.IsNullOrWhiteSpace(snap.GameId) ? "universal" : snap.GameId;
            var entry = PlayerMemoryStore.BuildEntryFromSessionSnapshot(snap);
            PlayerGameMemory updated;
            lock (_memoryIoLock)
                updated = _repo.RecordMatchToRollingMemory(gameId, snap.DisplayName, entry, _config.MemoryMatchWindow);

            var runningGameId = _coordinator.ActiveEngine?.ActiveGameId ?? string.Empty;
            if (string.IsNullOrEmpty(runningGameId) || string.Equals(runningGameId, gameId, StringComparison.OrdinalIgnoreCase))
            {
                GameProfile? prof;
                lock (_profilesLock)
                    prof = _profiles.FirstOrDefault(p => string.Equals(p.Manifest.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                SyncAssembledContext(gameId, prof, CoachContextDepth.RealtimePlay);
            }

            if (!_shutdownComplete)
            {
                AppendLog($"🧠 Memoria actualizada ({updated.Matches.Count}/{PlayerMemoryStore.ClampWindowSize(_config.MemoryMatchWindow)} partidas) para {snap.DisplayName}.");
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_shutdownComplete)
                        RefreshRollingMemoryUi();
                });
            }
        }
        catch (Exception ex)
        {
            if (!_shutdownComplete)
                AppendLog($"⚠ No se pudo guardar memoria de partida: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private void ApplyStoppedUiState()
    {
        _overlay.ClearHistory();
        SetRunningControlLocks(false);
        ResetStatsCards();
        RefreshMatchReviewButtonState();
        PauseBadge.Visibility = Visibility.Collapsed;
        TxtHeaderBadge.Text = "🛡 0% GPU · Anti-Cheat Safe";
        HeaderStatusText.Text = "Listo para jugar";
        _trayIcon?.UpdateTooltip("HUDMind · Listo para jugar");
    }

    private void ToggleMicViaHotkey()
    {
        if (!_coordinator.IsRunning || !_coordinator.SupportsLiveMic) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete || !_coordinator.IsRunning) return;
            string nextMode = _config.IsMicActive ? "coach_voice" : "conversation";
            ApplyCommunicationModeFromUi(nextMode);
            if (_config.EnableAudioCues && !_config.IsTextOnlyMode)
                Win32AudioCue.Play(_config.IsMicActive ? AudioCueKind.SoftOn : AudioCueKind.SoftOff);
            if (_config.EnableOverlay)
            {
                _overlay.ShowMessage(new OverlayCalloutMessage(
                    _config.IsMicActive ? "🎤 Micrófono ACTIVO" : "🔇 Micrófono SILENCIADO",
                    UrgencyLevel.Low,
                    "HUDMind Live",
                    2.0,
                    OverlayEntryKind.Status,
                    "HUD"));
            }
        });
    }

    private void BtnAnalyzeNow_Click(object sender, RoutedEventArgs e) =>
        _coordinator.TriggerOnDemandAnalysis();

    private void BtnPause_Click(object sender, RoutedEventArgs e) =>
        _coordinator.TogglePause();

    private void CmbCommMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbCommModeSettings is null) return;
        ApplyCommunicationModeFromUi(GetSelectedTag(CmbCommMode));
    }

    private void CmbCommModeSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbCommMode is null) return;
        ApplyCommunicationModeFromUi(GetSelectedTag(CmbCommModeSettings));
    }

    private void ApplyCommunicationModeFromUi(string mode)
    {
        _config.SetCommunicationMode(mode);
        _syncingUi = true;
        try
        {
            SelectByTag(CmbCommMode, _config.CommunicationMode);
            SelectByTag(CmbCommModeSettings, _config.CommunicationMode);
            SelectByTag(CmbLiveAutoRate, NormalizeLiveAutoRateBucket(_config.LiveAutoCoachSeconds));
        }
        finally
        {
            _syncingUi = false;
        }

        _repo.SaveUserConfig(_config);
        _coordinator.SetCommunicationMode(_config.CommunicationMode);

        if (!_coordinator.IsRunning)
        {
            string desc = _config.IsTextOnlyMode
                ? "💬 Solo texto (sin voz ni micrófono)"
                : _config.IsCoachVoiceOnlyMode
                    ? "🔊 Solo voz del coach (micrófono apagado)"
                    : "🎙 Charla (micrófono + voz del coach)";
            AppendLog($"Modo de comunicación configurado: {desc}.");
        }
    }

    private void TxtChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (!_chatSending)
                _ = SendChatInputAsync();
        }
    }

    private void BtnSendChat_Click(object sender, RoutedEventArgs e)
    {
        if (!_chatSending)
            _ = SendChatInputAsync();
    }

    private async Task SendChatInputAsync()
    {
        if (_chatSending || !_coordinator.IsRunning) return;
        var text = (TxtChatInput.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return;

        _chatSending = true;
        BtnSendChat.IsEnabled = false;
        try
        {
            if (await _coordinator.SendUserTextAsync(text))
                TxtChatInput.Clear();
        }
        catch (Exception ex)
        {
            AppendLog($"Error enviando chat: {SecretRedactor.Redact(ex.Message, GetAccountApiKeysSnapshot())}");
        }
        finally
        {
            _chatSending = false;
            BtnSendChat.IsEnabled = _coordinator.IsRunning;
            if (_coordinator.IsRunning)
                TxtChatInput.Focus();
        }
    }

    private void BtnClearFeed_Click(object sender, RoutedEventArgs e) =>
        _feedEntries.Clear();

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e) =>
        _logEntries.Clear();

    private void MenuCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LstLiveLogs.SelectedItem is string line && !string.IsNullOrEmpty(line))
        {
            try { System.Windows.Clipboard.SetText(line); } catch { }
        }
    }

    private void CmbNewProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || TxtNewLabel is null || TxtNewModel is null) return;
        var prov = CmbNewProvider.SelectedValue?.ToString() ?? "groq";
        var preset = ProviderRegistry.FindPreset(prov);
        if (preset is not null)
        {
            TxtNewLabel.Text = preset.DefaultLabel;
            TxtNewModel.Text = preset.DefaultModel;
            if (TxtNewBaseUrl is not null)
            {
                TxtNewBaseUrl.Clear();
                TxtNewBaseUrl.ToolTip = $"Opcional (default: {preset.BaseUrl}). Dejar vacío = usar URL oficial.";
            }
            if (CmbNewVisionOverride is not null)
                CmbNewVisionOverride.SelectedIndex = 0;
        }
    }

    private void LstAccounts_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        BeginEditSelectedAccount();

    private void BtnEditAccount_Click(object sender, RoutedEventArgs e) =>
        BeginEditSelectedAccount();

    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e) =>
        CancelAccountEdit();

    private void BeginEditSelectedAccount()
    {
        if (LstAccounts.SelectedItem is not AccountRowView row) return;
        var acc = row.Source;
        _editingAccountId = acc.Id;

        _syncingUi = true;
        try
        {
            CmbNewProvider.SelectedValue = acc.Provider;
            TxtNewLabel.Text = acc.Label;
            TxtNewModel.Text = acc.Model;
            TxtNewBaseUrl.Text = acc.BaseUrl;
            string visionTag = acc.SupportsVisionOverride switch
            {
                true => "vision",
                false => "text",
                _ => "auto"
            };
            SelectByTag(CmbNewVisionOverride, visionTag);
        }
        finally
        {
            _syncingUi = false;
        }

        PwdNewApiKey.Clear();
        PwdNewApiKey.ToolTip = "Editando: dejá vacío para conservar la API Key actual, o pegá una nueva";
        BtnAddAccount.Content = $"💾 Guardar #{acc.Priority}";
        BtnCancelEdit.Visibility = Visibility.Visible;
        ExpAccountAdvanced.IsExpanded = !string.IsNullOrWhiteSpace(acc.BaseUrl) || acc.SupportsVisionOverride.HasValue;
        TxtNewLabel.Focus();
    }

    private void CancelAccountEdit()
    {
        _editingAccountId = null;
        BtnAddAccount.Content = "+ Conectar IA";
        BtnCancelEdit.Visibility = Visibility.Collapsed;
        ExpAccountAdvanced.IsExpanded = false;
        PwdNewApiKey.Clear();
        PwdNewApiKey.ToolTip = "Pegá tu clave (oculta para seguridad en OBS/Discord)";
        CmbNewProvider_SelectionChanged(CmbNewProvider, null!);
    }

    private void BtnAddAccount_Click(object sender, RoutedEventArgs e)
    {
        var prov = CmbNewProvider.SelectedValue?.ToString() ?? "groq";
        var model = TxtNewModel.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            System.Windows.MessageBox.Show("Ingresá un nombre de modelo válido.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var customBaseUrl = (TxtNewBaseUrl.Text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(customBaseUrl))
        {
            bool allowLoopback = string.Equals(prov, "ollama", StringComparison.OrdinalIgnoreCase);
            var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(customBaseUrl, allowLoopback);
            if (!okUrl)
            {
                System.Windows.MessageBox.Show(errUrl, "BaseUrl inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        bool? visionOverride = GetSelectedTag(CmbNewVisionOverride) switch
        {
            "vision" => true,
            "text" => false,
            _ => null
        };

        var apiKey = PwdNewApiKey.Password.Trim();

        if (!string.IsNullOrEmpty(_editingAccountId))
        {
            string targetLabel = string.IsNullOrWhiteSpace(TxtNewLabel.Text) ? prov : TxtNewLabel.Text.Trim();
            bool missingRequiredKey = false;
            int updatedPriority = 0;
            bool updatedSupportsVision = false;
            List<ProviderAccount>? accountsToSave = null;
            lock (_accountsLock)
            {
                var existing = _accounts.FirstOrDefault(a => a.Id == _editingAccountId);
                if (existing is not null)
                {
                    if (!ProviderRegistry.IsKeylessAllowed(prov) &&
                        string.IsNullOrWhiteSpace(apiKey) &&
                        string.IsNullOrWhiteSpace(existing.ApiKey))
                    {
                        missingRequiredKey = true;
                    }
                    else
                    {
                        existing.Provider = prov;
                        existing.Label = targetLabel;
                        existing.Model = model;
                        existing.BaseUrl = customBaseUrl;
                        existing.SupportsVisionOverride = visionOverride;
                        if (!string.IsNullOrEmpty(apiKey))
                            existing.ApiKey = apiKey;
                        updatedPriority = existing.Priority;
                        updatedSupportsVision = existing.SupportsVision;
                        accountsToSave = [.. _accounts];
                    }
                }
            }

            if (missingRequiredKey)
            {
                System.Windows.MessageBox.Show("Este proveedor requiere una API Key.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (accountsToSave is not null)
            {
                CancelAccountEdit();
                RefreshAccountsList();
                _repo.SaveAccounts(accountsToSave);
                AppendLog($"Cuenta #{updatedPriority} actualizada: {targetLabel} [{prov}:{model} · {(updatedSupportsVision ? "Visión" : "OCR/Logs")}]");
                return;
            }
            CancelAccountEdit();
        }

        var acc = new ProviderAccount
        {
            Label = string.IsNullOrWhiteSpace(TxtNewLabel.Text) ? prov : TxtNewLabel.Text.Trim(),
            Provider = prov,
            Model = model,
            ApiKey = apiKey,
            BaseUrl = customBaseUrl,
            SupportsVisionOverride = visionOverride,
            Priority = 1,
            Enabled = true
        };
        if (!acc.IsReady())
        {
            System.Windows.MessageBox.Show("Pegá tu API Key para este proveedor.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        List<ProviderAccount> addedSnapshot;
        lock (_accountsLock)
        {
            _accounts.Insert(0, acc);
            addedSnapshot = [.. _accounts];
        }
        PwdNewApiKey.Clear();
        TxtNewBaseUrl.Clear();
        CmbNewVisionOverride.SelectedIndex = 0;
        ExpAccountAdvanced.IsExpanded = false;
        RefreshAccountsList();
        _repo.SaveAccounts(addedSnapshot);
        AppendLog($"Cuenta agregada en prioridad #1: {acc.Label} [{acc.Provider}:{acc.Model} · {(acc.SupportsVision ? "Visión" : "OCR/Logs")}]");
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedAccount(-1);
    private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedAccount(1);

    private void MoveSelectedAccount(int delta)
    {
        int idx = LstAccounts.SelectedIndex;
        int target = idx + delta;
        List<ProviderAccount> movedSnapshot;
        lock (_accountsLock)
        {
            if (idx < 0 || target < 0 || target >= _accounts.Count) return;
            (_accounts[idx], _accounts[target]) = (_accounts[target], _accounts[idx]);
            movedSnapshot = [.. _accounts];
        }
        RefreshAccountsList();
        LstAccounts.SelectedIndex = target;
        _repo.SaveAccounts(movedSnapshot);
    }

    private void BtnToggleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (LstAccounts.SelectedItem is not AccountRowView row) return;
        List<ProviderAccount> toggledSnapshot;
        lock (_accountsLock)
        {
            row.Source.Enabled = !row.Source.Enabled;
            toggledSnapshot = [.. _accounts];
        }
        RefreshAccountsList();
        _repo.SaveAccounts(toggledSnapshot);
    }

    private void BtnDeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstAccounts.SelectedIndex;
        if (idx < 0) return;
        bool deletingEdited;
        List<ProviderAccount> remainingSnapshot;
        lock (_accountsLock)
        {
            if (idx >= _accounts.Count) return;
            deletingEdited = _editingAccountId == _accounts[idx].Id;
            _accounts.RemoveAt(idx);
            remainingSnapshot = [.. _accounts];
        }
        if (deletingEdited)
            CancelAccountEdit();
        RefreshAccountsList();
        _repo.SaveAccounts(remainingSnapshot);
    }

    private async void BtnTestAccount_Click(object sender, RoutedEventArgs e)
    {
        if (LstAccounts.SelectedItem is not AccountRowView row) return;
        BtnTestAccount.IsEnabled = false;
        try
        {
            await using var adapter = ProviderRegistry.CreateHttpAdapter(row.Source);
            var (ok, msg) = await adapter.TestConnectionAsync();
            AppendLog($"Test [{row.Label}]: {(ok ? "OK" : "ERROR")} — {msg}");
            System.Windows.MessageBox.Show(msg, ok ? "Conexión Exitosa" : "Error de Conexión", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            var safe = SecretRedactor.Redact(ex.Message, [row.Source.ApiKey]);
            AppendLog($"Test [{row.Label}] falló: {safe}");
            System.Windows.MessageBox.Show(safe, "Error de Conexión", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnTestAccount.IsEnabled = true;
        }
    }

    private void BtnOpenGroq_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://console.groq.com/keys");
    private void BtnOpenGemini_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://aistudio.google.com/apikey");

    private void OpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"No se pudo abrir navegador: {ex.Message}");
        }
    }

    private void CmbRulesGame_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var nextId = CmbRulesGame.SelectedValue?.ToString();
        if (string.IsNullOrEmpty(nextId))
            return;

        if (_rulesDirty && !string.IsNullOrEmpty(_lastRulesGameId) && !string.Equals(_lastRulesGameId, nextId, StringComparison.OrdinalIgnoreCase))
        {
            var ans = System.Windows.MessageBox.Show(
                $"Tenés cambios sin guardar en '{_lastRulesGameId}'. ¿Descartarlos?",
                "HUDMind · Reglas sin guardar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes)
            {
                _syncingUi = true;
                try { CmbRulesGame.SelectedValue = _lastRulesGameId; }
                finally { _syncingUi = false; }
                return;
            }
        }

        _lastRulesGameId = nextId;
        var prof = FindProfileByIdLocked(nextId);
        _syncingUi = true;
        try { TxtCustomRules.Text = prof?.CustomRules ?? string.Empty; }
        finally { _syncingUi = false; }
        PopulateManifestFields(prof);
        _rulesDirty = false;
        UpdateRulesCounterUi();
    }

    private void PopulateManifestFields(GameProfile? prof)
    {
        if (TxtProfileExes is null || TxtProfileCooldown is null || TxtProfilePhash is null || BtnDeleteProfile is null)
            return;

        bool isUniversal = prof is null || string.Equals(prof.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase);
        TxtProfileExes.IsEnabled = !isUniversal;
        TxtProfileCooldown.IsEnabled = !isUniversal;
        TxtProfilePhash.IsEnabled = !isUniversal;
        BtnDeleteProfile.IsEnabled = !isUniversal;

        if (prof is null || isUniversal)
        {
            TxtProfileExes.Text = "(Cualquier juego en foreground)";
            TxtProfileCooldown.Text = "8.0";
            TxtProfilePhash.Text = "6";
            RefreshRollingMemoryUi("universal");
            return;
        }

        TxtProfileExes.Text = string.Join(", ", prof.Manifest.ProcessNames);
        TxtProfileCooldown.Text = prof.Manifest.CooldownSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        TxtProfilePhash.Text = prof.Manifest.PhashThreshold.ToString(CultureInfo.InvariantCulture);
        RefreshRollingMemoryUi(prof.Manifest.GameId);
    }

    private void RefreshRollingMemoryUi(string? gameId = null)
    {
        if (LblRollingMemorySummary is null || BtnResetGameMemory is null)
            return;

        var id = gameId ?? CmbRulesGame.SelectedValue?.ToString() ?? "universal";
        PlayerGameMemory mem;
        lock (_memoryIoLock)
            mem = _repo.LoadPlayerGameMemory(id);

        if (mem.WindowSize <= 0 || mem.Matches.Count == 0)
            mem.WindowSize = _config.MemoryMatchWindow;

        LblRollingMemorySummary.Text = PlayerMemoryStore.FormatForUiSummary(mem);
        BtnResetGameMemory.IsEnabled = mem.Matches.Count > 0;
    }

    private void BtnResetGameMemory_Click(object sender, RoutedEventArgs e)
    {
        var id = CmbRulesGame.SelectedValue?.ToString() ?? "universal";
        var prof = FindProfileByIdLocked(id);
        var displayName = prof?.Manifest.DisplayName ?? id;
        var ans = System.Windows.MessageBox.Show(
            $"¿Limpiar el historial de patrones aprendidos en las últimas partidas de '{displayName}'?",
            "HUDMind · Limpiar memoria del coach",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ans != MessageBoxResult.Yes)
            return;

        try
        {
            lock (_memoryIoLock)
                _repo.ResetPlayerGameMemory(id);
            _config.PreviousMatchFocus = string.Empty;
            _coordinator.RefreshPersona("memoria reseteada");
            RefreshRollingMemoryUi(id);
            AppendLog($"🧹 Memoria de últimas partidas limpiada para {displayName}.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(SecretRedactor.Redact(ex.Message), "Error al limpiar memoria", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TxtCustomRules_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingUi) _rulesDirty = true;
        UpdateRulesCounterUi();
    }

    private void UpdateRulesCounterUi()
    {
        if (LblRulesCount is null) return;
        int len = TxtCustomRules.Text.Length;
        LblRulesCount.Text = $"{len} / {ConfigRepository.MaxCustomRulesChars} caracteres (envío íntegro en tiempo real)";
        LblRulesCount.Foreground = new SolidColorBrush(
            len >= ConfigRepository.MaxCustomRulesChars - 40
                ? MediaColor.FromRgb(0xF5, 0x9E, 0x0B)
                : MediaColor.FromRgb(0x94, 0xA3, 0xB8));
    }

    private void BtnSaveRules_Click(object sender, RoutedEventArgs e)
    {
        var id = CmbRulesGame.SelectedValue?.ToString();
        var prof = FindProfileByIdLocked(id);
        if (prof is null) return;
        try
        {
            if (!string.Equals(prof.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase))
            {
                var exes = (TxtProfileExes.Text ?? string.Empty)
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var cdText = (TxtProfileCooldown.Text ?? "8").Trim().Replace(',', '.');
                if (!double.TryParse(cdText, NumberStyles.Float, CultureInfo.InvariantCulture, out double cooldown))
                {
                    System.Windows.MessageBox.Show("Pausa entre avisos inválida (ej: 8 o 10.0).", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse((TxtProfilePhash.Text ?? "6").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int phash))
                {
                    System.Windows.MessageBox.Show("Sensibilidad visual inválida (1 a 64).", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _repo.UpdateProfileManifest(prof, exes, cooldown, phash);
                PopulateManifestFields(prof);
            }

            _repo.SaveCustomRules(prof, TxtCustomRules.Text);
            _rulesDirty = false;
            _coordinator.RefreshPersona($"reglas {prof.Manifest.DisplayName}");
            AppendLog($"Reglas y perfil guardados/aplicados para '{prof.Manifest.DisplayName}'.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var id = CmbRulesGame.SelectedValue?.ToString();
        var prof = FindProfileByIdLocked(id);
        if (prof is null || string.Equals(prof.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase))
            return;

        var ans = System.Windows.MessageBox.Show(
            $"¿Eliminar permanentemente el juego '{prof.Manifest.DisplayName}'?",
            "HUDMind · Eliminar Juego",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (ans != MessageBoxResult.Yes)
            return;

        try
        {
            _repo.DeleteGameProfile(prof);
            GameProfile? nextProf;
            lock (_profilesLock)
            {
                _profiles.Remove(prof);
                nextProf = _profiles.FirstOrDefault();
            }

            var prevMain = CmbGameProfile.SelectedValue?.ToString();
            _rulesDirty = false;
            RefreshProfilePickers(prevMain, nextProf?.Manifest.GameId);

            _syncingUi = true;
            try
            {
                _lastRulesGameId = nextProf?.Manifest.GameId;
                TxtCustomRules.Text = nextProf?.CustomRules ?? string.Empty;
                PopulateManifestFields(nextProf);
            }
            finally
            {
                _syncingUi = false;
            }

            UpdateRulesCounterUi();
            AppendLog($"Perfil eliminado: {prof.Manifest.DisplayName} ({prof.Manifest.GameId})");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(SecretRedactor.Redact(ex.Message), "Error al eliminar perfil", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnBrowseNewExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Elegí el ejecutable (.exe) de tu juego",
            Filter = "Ejecutables de juegos (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var fileName = Path.GetFileName(dlg.FileName);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            TxtNewProfExe.Text = fileName;
            if (string.IsNullOrWhiteSpace(TxtNewProfName.Text))
                TxtNewProfName.Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                    Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' '));
        }
    }

    private void BtnPromptChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string snippet || string.IsNullOrWhiteSpace(snippet))
            return;

        var current = TxtCustomRules.Text ?? string.Empty;
        if (current.Contains(snippet, StringComparison.OrdinalIgnoreCase))
            return;

        var separator = string.IsNullOrWhiteSpace(current)
            ? string.Empty
            : (current.EndsWith('\n') ? string.Empty : Environment.NewLine);
        var next = current + separator + snippet;
        if (next.Length > ConfigRepository.MaxCustomRulesChars)
        {
            System.Windows.MessageBox.Show(
                $"Límite de {ConfigRepository.MaxCustomRulesChars} caracteres alcanzado.",
                "HUDMind",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        TxtCustomRules.Text = next;
        TxtCustomRules.CaretIndex = next.Length;
        TxtCustomRules.Focus();
    }

    private void BtnCreateProfile_Click(object sender, RoutedEventArgs e)
    {
        var rawName = (TxtNewProfName.Text ?? string.Empty).Trim();
        var exeList = (TxtNewProfExe.Text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (string.IsNullOrWhiteSpace(rawName))
        {
            System.Windows.MessageBox.Show("Escribí el nombre del juego (ej: Apex Legends).", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtNewProfName.Focus();
            return;
        }
        if (exeList.Length == 0)
        {
            System.Windows.MessageBox.Show("Indicá el archivo .exe del juego (ej: r5apex.exe) o usá '📂 Elegir .exe'.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtNewProfExe.Focus();
            return;
        }

        if (_rulesDirty)
        {
            var ans = System.Windows.MessageBox.Show(
                $"Tenés cambios sin guardar en '{_lastRulesGameId}'. ¿Descartarlos para editar el nuevo perfil?",
                "HUDMind · Reglas sin guardar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes)
                return;
        }

        var rawId = CoachFeedFormatter.BuildSafeGameId(rawName, GetProfilesSnapshot().Select(p => p.Manifest.GameId));
        try
        {
            var created = _repo.CreateNewGameProfile(rawId, rawName, exeList);
            bool duplicate;
            lock (_profilesLock)
            {
                duplicate = _profiles.Any(p => string.Equals(p.Manifest.GameId, created.Manifest.GameId, StringComparison.OrdinalIgnoreCase));
                if (!duplicate)
                    _profiles.Add(created);
            }
            if (duplicate)
            {
                System.Windows.MessageBox.Show($"Ya existe un juego similar ('{created.Manifest.GameId}').", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var prevActiveGame = CmbGameProfile.SelectedValue?.ToString();
            _rulesDirty = false;
            RefreshProfilePickers(prevActiveGame, created.Manifest.GameId);

            _syncingUi = true;
            try
            {
                _lastRulesGameId = created.Manifest.GameId;
                TxtCustomRules.Text = created.CustomRules;
                PopulateManifestFields(created);
            }
            finally
            {
                _syncingUi = false;
            }

            UpdateRulesCounterUi();
            TxtNewProfName.Clear();
            TxtNewProfExe.Clear();
            AppendLog($"Juego agregado: {created.Manifest.DisplayName}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(SecretRedactor.Redact(ex.Message), "No se pudo agregar el juego", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CmbQuickGender_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbVoiceGender is null) return;
        ApplyVoiceSettingsFromUi(GetSelectedTag(CmbQuickGender), _config.VoiceStyle);
    }

    private void CmbVoiceSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbVoiceGender is null || CmbVoiceStyle is null || CmbQuickGender is null) return;
        ApplyVoiceSettingsFromUi(GetSelectedTag(CmbVoiceGender), GetSelectedTag(CmbVoiceStyle));
    }

    private void CmbVoiceRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbVoiceRate is null) return;
        string rate = GetSelectedTag(CmbVoiceRate, "+5%");
        _config.TtsRate = rate;
        _repo.SaveUserConfig(_config);
        _sharedVoice.SetSpeechRate(rate);
        _coordinator.ActiveEngine?.UpdateAudioLive(_config);
        AppendLog($"Velocidad de voz ajustada: {rate}.");
    }

    private static string NormalizeVoiceRateBucket(string? rate)
    {
        var clean = (rate ?? "+5%").Trim().TrimEnd('%').Replace(',', '.');
        if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            return "+5%";
        return pct switch
        {
            <= -5.0 => "-10%",
            <= 2.0 => "+0%",
            <= 10.0 => "+5%",
            <= 20.0 => "+15%",
            _ => "+25%"
        };
    }

    private static string NormalizeLiveAutoRateBucket(double seconds) => seconds switch
    {
        <= 0.1 => "0",
        <= 5.0 => "4",
        <= 8.0 => "6",
        _ => "10"
    };

    private void ApplyVoiceSettingsFromUi(string gender, string style)
    {
        _syncingUi = true;
        try
        {
            SelectByTag(CmbQuickGender, gender);
            SelectByTag(CmbVoiceGender, gender);
            SelectByTag(CmbVoiceStyle, style);
        }
        finally
        {
            _syncingUi = false;
        }

        _config.VoiceGender = gender;
        _config.VoiceStyle = style;
        _repo.SaveUserConfig(_config);
        _sharedVoice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, gender, style);

        _coordinator.ApplyLiveSettings(_config);
        var voiceLabel = _coordinator.ActiveEngine is { IsRunning: true } eng ? eng.ActiveVoiceDisplay : style;
        AppendLog($"Voz actualizada: {(gender == "female" ? "👩 Mujer" : "👨 Hombre")} ({voiceLabel}).");
    }

    private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || LblVolumePct is null) return;
        double vol = Math.Round(SldVolume.Value, 2);
        LblVolumePct.Text = $"{(int)Math.Round(vol * 100)}%";
        _config.TtsVolume = vol;
        _sharedVoice.UpdateSettings(_config.VoiceLanguage, vol, _config.VoiceGender, _config.VoiceStyle);
        _coordinator.ActiveEngine?.UpdateAudioLive(_config);
    }

    private void UpdateOverlaySliderLabels()
    {
        if (LblOverlayScale is null || LblOverlayOpacity is null)
            return;
        string style = CmbOverlayStyle is not null ? GetSelectedTag(CmbOverlayStyle, _config.ResolveOverlayStyle()) : _config.ResolveOverlayStyle();
        int scalePct = (int)Math.Round(SldOverlayScale.Value * 100);
        int opacityPct = (int)Math.Round(SldOverlayOpacity.Value * 100);
        LblOverlayScale.Text = $"{scalePct}%";
        LblOverlayOpacity.Text = style == UserConfig.OverlayStyleTransparentText
            ? "0% (Sin caja)"
            : opacityPct == 0
                ? "0% (Transparente)"
                : $"{opacityPct}%";
    }

    private void SyncOverlayVisualSettingsFromUi()
    {
        if (CmbOverlayStyle is not null)
        {
            _config.OverlayStyle = UserConfig.NormalizeOverlayStyle(GetSelectedTag(CmbOverlayStyle, _config.ResolveOverlayStyle()));
            _config.OverlayCompact = _config.OverlayStyle != UserConfig.OverlayStyleCard;
        }
        if (CmbOverlayFeedLines is not null && int.TryParse(GetSelectedTag(CmbOverlayFeedLines, "3"), out int maxMsg))
            _config.OverlayMaxMessages = Math.Clamp(maxMsg, 1, 4);
        if (CmbOverlayPos is not null)
            _config.OverlayPosition = GetSelectedTag(CmbOverlayPos, "top-right");
        if (CmbMonitor?.SelectedValue is int mon)
            _config.CaptureMonitor = mon;
        if (SldOverlayScale is not null)
            _config.OverlayScale = Math.Round(SldOverlayScale.Value, 2);
        if (SldOverlayOpacity is not null)
            _config.OverlayOpacity = Math.Round(SldOverlayOpacity.Value, 2);
        UpdateOverlaySliderLabels();
        ConfigureOverlayFromCurrentConfig();
    }

    private void CmbOverlayVisual_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || LblOverlayScale is null) return;
        SyncOverlayVisualSettingsFromUi();
    }

    private void SldOverlayVisual_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || LblOverlayScale is null || LblOverlayOpacity is null) return;
        SyncOverlayVisualSettingsFromUi();
    }

    private void BtnGoAccounts_Click(object sender, RoutedEventArgs e) =>
        _navMediator.SelectTab(1);

    private void BtnGoSettings_Click(object sender, RoutedEventArgs e) =>
        _navMediator.SelectTab(3);

    private void BtnDismissOnboarding_Click(object sender, RoutedEventArgs e)
    {
        _onboardingDismissed = true;
        RefreshOnboardingBanner();
    }

    private async void BtnMatchReview_Click(object sender, RoutedEventArgs e)
    {
        bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        var (gameName, digest, _) = _coordinator.SnapshotLastMatchSession(isEn);
        if (string.IsNullOrWhiteSpace(digest))
        {
            AppendLog("ℹ️ Todavía no hay consejos registrados en esta sesión para resumir.");
            return;
        }
        var accountsSnapshot = GetAccountsSnapshot();
        if (!accountsSnapshot.Any(a => a.IsReady()))
        {
            AppendLog("⚠ Agregá una cuenta IA habilitada para generar el resumen post-partida.");
            return;
        }

        BtnMatchReview.IsEnabled = false;
        try
        {
            var (_, metaGameId, _, _) = _coordinator.GetLastMatchMeta();
            var targetId = string.IsNullOrWhiteSpace(metaGameId)
                ? (CmbGameProfile.SelectedValue?.ToString() ?? "universal")
                : metaGameId;
            var targetProfile = FindProfileByIdLocked(targetId);
            SyncAssembledContext(targetId, targetProfile, CoachContextDepth.AnalyticalReflection);

            AppendLog($"📊 Analizando partida reciente ({gameName})...");
            var (review, ms, model) = await _coordinator.GeneratePostMatchReviewAsync(accountsSnapshot, _config, AppendLog, profile: targetProfile);
            if (string.IsNullOrWhiteSpace(review))
            {
                AppendLog($"No se pudo generar el resumen post-partida [{model}].");
                return;
            }

            AppendLog($"📊 === RESUMEN POST-PARTIDA ({gameName}, {ms:F0}ms · {model}) ===");
            foreach (var rawLine in review.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                AppendLog($"   {rawLine.Trim()}");

            try
            {
                var savedPath = _repo.SavePostMatchReview(gameName, model, review);
                AppendLog($"💾 Resumen guardado: {savedPath}");

                var (matchId, matchGameId, _, matchCount) = _coordinator.GetLastMatchMeta();
                var targetGameId = string.IsNullOrWhiteSpace(matchGameId) ? targetId : matchGameId;
                var aiEntry = PlayerMemoryStore.BuildEntryFromAiReview(matchId, targetGameId, gameName, matchCount, review);
                PlayerGameMemory updatedMem;
                lock (_memoryIoLock)
                    updatedMem = _repo.RecordMatchToRollingMemory(
                        targetGameId,
                        gameName,
                        aiEntry,
                        _config.MemoryMatchWindow,
                        Path.GetFileName(savedPath));

                SyncAssembledContext(targetGameId, targetProfile, CoachContextDepth.RealtimePlay);
                _coordinator.RefreshPersona("resumen post-partida guardado");
                RefreshRollingMemoryUi();
                AppendLog($"🧠 Memoria .md y rolling consolidados ({updatedMem.Matches.Count}/{PlayerMemoryStore.ClampWindowSize(_config.MemoryMatchWindow)} partidas).");
            }
            catch (Exception saveEx)
            {
                AppendLog($"⚠ No se pudo guardar resumen en disco: {SecretRedactor.Redact(saveEx.Message)}");
            }

            if (_closingInProgress || _shutdownComplete)
                return;

            if (_config.IsCoachVoiceActive)
            {
                string spoken = review.Replace("*", "").Replace("#", "").Trim();
                if (spoken.Length > 280) spoken = spoken[..280];
                SpeakWithSharedVoiceAsync(spoken);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Error generando resumen post-partida: {SecretRedactor.Redact(ex.Message, GetAccountApiKeysSnapshot())}");
        }
        finally
        {
            if (!_closingInProgress && !_shutdownComplete)
                RefreshMatchReviewButtonState();
        }
    }

    private void BtnOpenReviews_Click(object sender, RoutedEventArgs e) =>
        OpenSafeDirectory(_repo.ReviewsDir, "resúmenes");

    private void BtnOpenGameMemory_Click(object sender, RoutedEventArgs e) =>
        OpenSafeDirectory(_repo.MemoryDir, "memoria (.md)");

    private void BtnOpenSoulFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_repo.CoachSoulPath))
                _repo.SaveCoachSoulMarkdown(TxtCoachSoul.Text);
            if (!File.Exists(_repo.PlayerProfilePath))
                _repo.SavePlayerProfileMarkdown(TxtPlayerProfile.Text);
        }
        catch { }
        OpenSafeDirectory(_repo.ConfigDir, "config (.md)");
    }

    private void OpenSafeDirectory(string dirPath, string label)
    {
        try
        {
            var parent = Path.GetDirectoryName(dirPath);
            if ((!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) ||
                (Directory.Exists(dirPath) && (File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) != 0))
            {
                AppendLog($"⚠ Seguridad: carpeta de {label} es un enlace simbólico no permitido.");
                return;
            }
            Directory.CreateDirectory(dirPath);
            Process.Start(new ProcessStartInfo(dirPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"No se pudo abrir carpeta de {label}: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning)
        {
            System.Windows.MessageBox.Show("Detené primero el Coach Live para hacer una prueba aislada de micrófono.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedMic = GetSelectedTag(CmbMicDevice, string.Empty);
        BtnTestMic.IsEnabled = false;
        AppendLog($"🎤 Probando micrófono '{(string.IsNullOrEmpty(selectedMic) ? "Predeterminado" : selectedMic)}' por 1 segundo (hablá ahora)...");

        try
        {
            var probe = await AudioDiagnosticsService.ProbeMicrophoneAsync(selectedMic, durationMs: 1000);
            AppendLog(probe.Opened ? probe.SummaryMessage : $"⚠ {probe.SummaryMessage}");
            System.Windows.MessageBox.Show(probe.SummaryMessage, "Prueba de Micrófono", MessageBoxButton.OK, probe.Opened ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            BtnTestMic.IsEnabled = true;
        }
    }

    private void BtnInstallGsi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (count, msg) = _coordinator.InstallValveGsiConfigs();
            AppendLog($"🎮 GSI Valve: {msg}");
            System.Windows.MessageBox.Show(msg, "Instalación GSI Oficial", MessageBoxButton.OK, count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            var safeErr = $"No se pudo instalar GSI: {SecretRedactor.Redact(ex.Message)}";
            AppendLog($"⚠ {safeErr}");
            System.Windows.MessageBox.Show(safeErr, "Instalación GSI Oficial", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool ApplyConfigFromUi(bool validateHotkeys = false)
    {
        if (validateHotkeys)
        {
            if (!HotkeyParser.TryParse(TxtPauseKey.Text, out _, out _, out var pNorm))
            {
                System.Windows.MessageBox.Show($"Tecla Pausar inválida: '{TxtPauseKey.Text}'. Usá ej: F9, Ctrl+Shift+P, Insert.", "Atajo inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!HotkeyParser.TryParse(TxtAnalyzeKey.Text, out _, out _, out var aNorm))
            {
                System.Windows.MessageBox.Show($"Tecla Analizar inválida: '{TxtAnalyzeKey.Text}'. Usá ej: F10, Alt+A.", "Atajo inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!HotkeyParser.TryParse(TxtMicKey.Text, out _, out _, out var mNorm))
            {
                System.Windows.MessageBox.Show($"Tecla Mic inválida: '{TxtMicKey.Text}'. Usá ej: F8, Ctrl+M.", "Atajo inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!HotkeyParser.TryParsePushToTalkKey(TxtPttKey.Text, out _, out var pttNorm))
            {
                System.Windows.MessageBox.Show($"Tecla Push-to-Talk inválida: '{TxtPttKey.Text}'. Usá ej: V, Alt, CapsLock, Space, Mouse4, Mouse5.", "Push-to-Talk inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (pNorm == aNorm || pNorm == mNorm || aNorm == mNorm)
            {
                System.Windows.MessageBox.Show("Las 3 teclas rápidas (Pausar, Analizar, Mic) no pueden repetirse.", "Conflicto de atajos", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            TxtPauseKey.Text = pNorm;
            TxtAnalyzeKey.Text = aNorm;
            TxtMicKey.Text = mNorm;
            TxtPttKey.Text = pttNorm;
        }

        _config.CoachMode = GetSelectedTag(CmbCoachMode);
        _config.PlayerSkillTier = GetSelectedTag(CmbSkillTier);
        _config.PlayerRankLabel = (TxtRankLabel.Text ?? string.Empty).Trim();
        _config.CoachStyle = GetSelectedTag(CmbCoachStyle);
        _config.LiveAutoCoachSeconds = double.TryParse(
            GetSelectedTag(CmbLiveAutoRate),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double autoSec)
            ? autoSec
            : 6.0;
        _config.VoiceGender = GetSelectedTag(CmbVoiceGender);
        _config.VoiceStyle = GetSelectedTag(CmbVoiceStyle);
        _config.TtsRate = GetSelectedTag(CmbVoiceRate, "+5%");
        _config.UseLiveConversational = GetSelectedTag(CmbEngineType) == "live";
        _config.SetCommunicationMode(GetSelectedTag(CmbCommMode));
        _syncingUi = true;
        try
        {
            SelectByTag(CmbLiveAutoRate, NormalizeLiveAutoRateBucket(_config.LiveAutoCoachSeconds));
        }
        finally
        {
            _syncingUi = false;
        }
        _config.MicInputMode = GetSelectedTag(CmbMicInputMode, "open_mic");
        if (HotkeyParser.TryParsePushToTalkKey(TxtPttKey.Text, out _, out var pttSafe)) _config.PushToTalkKey = pttSafe;
        _config.AudioInputDeviceName = GetSelectedTag(CmbMicDevice, string.Empty);
        _config.AudioOutputDeviceName = GetSelectedTag(CmbSpeakerDevice, string.Empty);
        _config.TextLanguage = GetSelectedTag(CmbLanguage, "es");
        _config.VoiceLanguage = _config.TextLanguage;
        _config.TokenEfficiencyMode = GetSelectedTag(CmbEfficiency, "eco");
        _config.MemoryMatchWindow = int.TryParse(GetSelectedTag(CmbMemoryWindow, "10"), out int memWin)
            ? PlayerMemoryStore.ClampWindowSize(memWin)
            : PlayerMemoryStore.DefaultWindowSize;
        _config.OverlayStyle = UserConfig.NormalizeOverlayStyle(GetSelectedTag(CmbOverlayStyle, UserConfig.OverlayStyleDiscordPill));
        _config.OverlayCompact = _config.OverlayStyle != UserConfig.OverlayStyleCard;
        _config.OverlayMaxMessages = int.TryParse(GetSelectedTag(CmbOverlayFeedLines, "3"), out int ovMax)
            ? Math.Clamp(ovMax, 1, 4)
            : 3;
        _config.OverlayPosition = GetSelectedTag(CmbOverlayPos, "top-right");
        _config.CaptureMonitor = CmbMonitor.SelectedValue is int m ? m : 1;
        _config.EnableOverlay = ChkEnableOverlay.IsChecked == true;
        _config.EnableHotkeys = ChkEnableHotkeys.IsChecked == true;
        _config.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
        _config.EnableAudioCues = ChkEnableAudioCues.IsChecked == true;
        _config.CheckUpdatesOnStartup = ChkCheckUpdatesOnStartup.IsChecked == true;
        if (HotkeyParser.TryParse(TxtPauseKey.Text, out _, out _, out var pSafe)) _config.HotkeyTogglePause = pSafe;
        if (HotkeyParser.TryParse(TxtAnalyzeKey.Text, out _, out _, out var aSafe)) _config.HotkeyAnalyzeNow = aSafe;
        if (HotkeyParser.TryParse(TxtMicKey.Text, out _, out _, out var mSafe)) _config.HotkeyToggleMic = mSafe;
        _config.OverlayScale = Math.Round(SldOverlayScale.Value, 2);
        _config.OverlayOpacity = Math.Round(SldOverlayOpacity.Value, 2);
        _config.TtsVolume = Math.Round(SldVolume.Value, 2);
        return true;
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyConfigFromUi(validateHotkeys: true))
            return;

        RefreshMonitorsList();
        _repo.SaveUserConfig(_config);
        _repo.SaveCoachSoulMarkdown(TxtCoachSoul.Text);
        _repo.SavePlayerProfileMarkdown(TxtPlayerProfile.Text);
        var activeGameId = CmbGameProfile.SelectedValue?.ToString() ?? "universal";
        var activeProf = FindProfileByIdLocked(activeGameId);
        SyncAssembledContext(activeGameId, activeProf, CoachContextDepth.RealtimePlay);
        ConfigureOverlayFromCurrentConfig();
        if (!_config.EnableOverlay)
            _overlay.HideImmediately();

        _sharedVoice.SetOutputDevice(_config.AudioOutputDeviceName);
        _sharedVoice.SetSpeechRate(_config.TtsRate);
        _sharedVoice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
        _coordinator.ApplyLiveSettings(_config);
        RefreshHotkeyButtonLabels();
        _coordinator.RegisterHotkeys(_config, ToggleMicViaHotkey, AppendLog);
        AppendLog($"Preferencias guardadas y aplicadas en vivo (Nivel: {_config.PlayerSkillTier}, Mic: {(_config.IsPushToTalkMode ? $"PTT {_config.PushToTalkKey}" : "Abierto")}).");
    }

    private void BtnPreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyConfigFromUi()) return;
        ConfigureOverlayFromCurrentConfig();
        if (_config.EnableOverlay)
        {
            _overlay.ClearHistory();
            if (_config.ResolveOverlayMaxMessages() > 1)
            {
                _overlay.ShowMessage(new OverlayCalloutMessage(
                    "¿Dónde me paro ahora?",
                    UrgencyLevel.Low,
                    "Rocket League",
                    7.0,
                    OverlayEntryKind.UserMessage,
                    "Vos"));
            }
            _overlay.ShowMessage(new OverlayCalloutMessage(
                "Rotá segundo palo, tenés 18 de boost.",
                UrgencyLevel.High,
                "Rocket League",
                7.0,
                OverlayEntryKind.CoachFinal,
                "Coach"));
        }

        if (!_config.IsCoachVoiceActive)
        {
            AppendLog("Preview mostrado solo en overlay (estás en modo 'Solo texto').");
            return;
        }

        SpeakWithSharedVoiceAsync(
            "Rotá segundo palo, tenés dieciocho de boost.",
            ms => AppendLog($"Preview Voz [{_sharedVoice.EngineName}] ({_config.TtsRate}) disparado en {ms:F1} ms."));
    }

    private void SpeakWithSharedVoiceAsync(string text, Action<double>? onDispatched = null)
    {
        if (_closingInProgress || _shutdownComplete || string.IsNullOrWhiteSpace(text))
            return;

        if (_coordinator.IsRunning)
        {
            AppendLog("ℹ️ Hay una sesión de coach activa: detenela antes de reproducir vista previa o resumen hablado.");
            return;
        }

        string outputDevice = _config.AudioOutputDeviceName;
        string lang = _config.VoiceLanguage;
        string gender = _config.VoiceGender;
        string style = _config.VoiceStyle;
        string rate = _config.TtsRate;
        double vol = _config.TtsVolume;
        _ = Task.Run(() =>
        {
            try
            {
                if (_closingInProgress || _shutdownComplete) return;
                _sharedVoice.SetOutputDevice(outputDevice);
                _sharedVoice.SetSpeechRate(rate);
                _sharedVoice.WarmUp(lang, vol, gender, style);
                if (_closingInProgress || _shutdownComplete) return;
                _sharedVoice.UpdateSettings(lang, vol, gender, style);
                double ms = _sharedVoice.SpeakInstant(text);
                if (!_closingInProgress && !_shutdownComplete)
                    onDispatched?.Invoke(ms);
            }
            catch (ObjectDisposedException) { }
            catch (Exception) { }
        });
    }

    private static string GetSelectedTag(System.Windows.Controls.ComboBox cb, string fallback = "auto") =>
        (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectByTag(System.Windows.Controls.ComboBox cb, string tag)
    {
        foreach (ComboBoxItem item in cb.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                cb.SelectedItem = item;
                return;
            }
        }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_shutdownComplete || _checkingUpdates || _downloadingUpdate)
            return;

        _checkingUpdates = true;
        BtnCheckUpdates.IsEnabled = false;
        BtnVersionCheck.IsEnabled = false;
        try
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource();

            var result = await _updateService.CheckForUpdatesAsync(ct: _updateCts.Token);
            if (_shutdownComplete)
                return;

            if (result.IsUpdateAvailable && result.Update is not null)
            {
                if (!manual && string.Equals(result.Update.TagName, _dismissedUpdateTag, StringComparison.OrdinalIgnoreCase))
                    return;

                bool alreadyDownloadedSameTag =
                    !string.IsNullOrEmpty(_downloadedUpdatePath) &&
                    File.Exists(_downloadedUpdatePath) &&
                    string.Equals(_availableUpdate?.TagName, result.Update.TagName, StringComparison.OrdinalIgnoreCase);

                if (alreadyDownloadedSameTag)
                {
                    UpdateBanner.Visibility = Visibility.Visible;
                    return;
                }

                if (manual)
                    _dismissedUpdateTag = null;

                ShowAvailableUpdateBanner(result.Update);
                AppendLog($"🚀 Nueva actualización disponible: {result.Update.TagName} (actual: v{GitHubReleaseUpdateService.FormatVersion(result.Update.CurrentVersion)}).");
            }
            else if (manual)
            {
                AppendLog($"ℹ Actualizaciones: {result.StatusMessage}");
                System.Windows.MessageBox.Show(
                    result.StatusMessage,
                    "HUDMind · Actualizaciones",
                    MessageBoxButton.OK,
                    result.WasRateLimited ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored on shutdown or superseded check
        }
        catch (Exception ex)
        {
            if (manual && !_shutdownComplete)
            {
                var safe = SecretRedactor.Redact(ex.Message);
                AppendLog($"⚠ Error buscando actualizaciones: {safe}");
                System.Windows.MessageBox.Show(
                    $"No se pudo consultar GitHub Releases:\n{safe}",
                    "HUDMind · Actualizaciones",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            _checkingUpdates = false;
            if (!_shutdownComplete)
            {
                BtnCheckUpdates.IsEnabled = !_downloadingUpdate;
                BtnVersionCheck.IsEnabled = !_downloadingUpdate;
            }
        }
    }

    private void ShowAvailableUpdateBanner(AppUpdateInfo update)
    {
        _availableUpdate = update;
        _downloadedUpdatePath = null;

        UpdateBanner.Background = new SolidColorBrush(MediaColor.FromRgb(0x0C, 0x4A, 0x6E));
        UpdateBanner.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x38, 0xBD, 0xF8));
        TxtUpdateBannerTitle.Text =
            $"🚀 Nueva actualización disponible: {update.TagName} (tenés v{GitHubReleaseUpdateService.FormatVersion(update.CurrentVersion)})";

        var notesSummary = GitHubReleaseUpdateService.SummarizeReleaseNotes(update.ReleaseNotes);
        if (update.PreferredAsset is not null)
        {
            var sizeMb = update.PreferredAsset.SizeBytes > 0
                ? $" ({update.PreferredAsset.SizeBytes / (1024.0 * 1024.0):F1} MB)"
                : string.Empty;
            TxtUpdateBannerDetails.Text = string.IsNullOrEmpty(notesSummary)
                ? $"{update.PreferredAsset.Name}{sizeMb} · Tocá 'Descargar actualización' para bajar el nuevo .exe."
                : $"{update.PreferredAsset.Name}{sizeMb} · {notesSummary}";
        }
        else
        {
            TxtUpdateBannerDetails.Text = string.IsNullOrEmpty(notesSummary)
                ? "Tocá 'Descargar actualización' para abrir la release oficial en GitHub."
                : notesSummary;
        }

        TxtUpdateBannerDetails.ToolTip = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? null : update.ReleaseNotes;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Value = 0;

        BtnDownloadUpdate.Content = update.PreferredAsset is not null
            ? "⬇ Descargar actualización"
            : "⬇ Descargar desde GitHub";
        BtnDownloadUpdate.Visibility = Visibility.Visible;
        BtnDownloadUpdate.IsEnabled = true;
        BtnLaunchUpdate.Visibility = Visibility.Collapsed;
        BtnOpenUpdateFolder.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;

        BtnVersionCheck.Content = $"⬆ {update.TagName}";
        BtnVersionCheck.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x10, 0xB9, 0x81));
        BtnVersionCheck.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x10, 0xB9, 0x81));
    }

    private async void BtnDownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _downloadingUpdate || _shutdownComplete)
            return;

        if (_availableUpdate.PreferredAsset is null)
        {
            OpenExternalUrl(_availableUpdate.ReleaseHtmlUrl);
            return;
        }

        var asset = _availableUpdate.PreferredAsset;
        var baseName = Path.GetFileNameWithoutExtension(asset.Name);
        var suggestedName = asset.Name.Contains(_availableUpdate.TagName, StringComparison.OrdinalIgnoreCase)
            ? asset.Name
            : $"{baseName}-{_availableUpdate.TagName}.exe";

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Guardar HUDMind {_availableUpdate.TagName}",
            InitialDirectory = KnownFolders.GetDownloadsDirectory(),
            FileName = suggestedName,
            Filter = "Ejecutable Windows (*.exe)|*.exe",
            DefaultExt = ".exe",
            AddExtension = true
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var currentExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(currentExe) &&
            string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(
                "Windows no permite sobrescribir el .exe que está ejecutándose ahora. Guardalo con otro nombre o en Descargas.",
                "HUDMind · Actualizaciones",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _downloadingUpdate = true;
        BtnDownloadUpdate.IsEnabled = false;
        BtnCheckUpdates.IsEnabled = false;
        BtnVersionCheck.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = asset.SizeBytes <= 0;
        UpdateProgressBar.Value = 0;
        TxtUpdateBannerDetails.Text = $"Iniciando descarga de {asset.Name}...";

        try
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource();

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (_shutdownComplete) return;
                if (p.TotalBytes is > 0)
                {
                    double pct = Math.Clamp(p.BytesReceived * 100.0 / p.TotalBytes.Value, 0, 100);
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = pct;
                    TxtUpdateBannerDetails.Text =
                        $"Descargando {asset.Name}: {p.BytesReceived / (1024.0 * 1024.0):F1} / {p.TotalBytes.Value / (1024.0 * 1024.0):F1} MB ({pct:F0}%)";
                }
                else
                {
                    UpdateProgressBar.IsIndeterminate = true;
                    TxtUpdateBannerDetails.Text =
                        $"Descargando {asset.Name}: {p.BytesReceived / (1024.0 * 1024.0):F1} MB...";
                }
            });

            var result = await _updateService.DownloadAssetAsync(
                asset,
                dlg.FileName,
                _availableUpdate.ChecksumAsset,
                progress,
                _updateCts.Token);

            if (_shutdownComplete)
                return;

            UpdateProgressBar.Visibility = Visibility.Collapsed;
            if (result.Success)
            {
                _downloadedUpdatePath = result.FilePath;
                UpdateBanner.Background = new SolidColorBrush(MediaColor.FromRgb(0x06, 0x4E, 0x3B));
                UpdateBanner.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x10, 0xB9, 0x81));
                TxtUpdateBannerTitle.Text = $"✅ Actualización {_availableUpdate.TagName} lista para usar";
                TxtUpdateBannerDetails.Text = result.Sha256Verified
                    ? $"Guardado en {result.FilePath} · Integridad SHA-256 verificada."
                    : $"Guardado en {result.FilePath}.";
                BtnDownloadUpdate.Visibility = Visibility.Collapsed;
                BtnLaunchUpdate.Visibility = Visibility.Visible;
                BtnOpenUpdateFolder.Visibility = Visibility.Visible;
                AppendLog($"✅ {result.Message} ({result.FilePath})");
            }
            else
            {
                TxtUpdateBannerDetails.Text = $"⚠ {result.Message}";
                BtnDownloadUpdate.IsEnabled = true;
                AppendLog($"⚠ Error descargando actualización: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_shutdownComplete)
            {
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                TxtUpdateBannerDetails.Text = "Descarga cancelada.";
                BtnDownloadUpdate.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            if (!_shutdownComplete)
            {
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                var safe = SecretRedactor.Redact(ex.Message);
                TxtUpdateBannerDetails.Text = $"⚠ Error descargando: {safe}";
                BtnDownloadUpdate.IsEnabled = true;
                AppendLog($"⚠ Error descargando actualización: {safe}");
            }
        }
        finally
        {
            _downloadingUpdate = false;
            if (!_shutdownComplete)
            {
                BtnCheckUpdates.IsEnabled = true;
                BtnVersionCheck.IsEnabled = true;
            }
        }
    }

    private async void BtnLaunchUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_closingInProgress || _shutdownComplete || string.IsNullOrWhiteSpace(_downloadedUpdatePath) || !File.Exists(_downloadedUpdatePath))
            return;

        BtnLaunchUpdate.IsEnabled = false;
        try
        {
            bool wasRunning = _coordinator.IsRunning;
            if (wasRunning)
                await _coordinator.StopAsync();
            if (_closingInProgress || _shutdownComplete)
                return;
            if (wasRunning)
                ApplyStoppedUiState();
            (System.Windows.Application.Current as App)?.ReleaseSingleInstanceMutex();
            Process.Start(new ProcessStartInfo(_downloadedUpdatePath) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            BtnLaunchUpdate.IsEnabled = true;
            AppendLog($"⚠ No se pudo abrir la nueva versión: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private void BtnOpenUpdateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_downloadedUpdatePath) || !File.Exists(_downloadedUpdatePath))
            return;

        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add($"/select,{_downloadedUpdatePath}");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppendLog($"⚠ No se pudo abrir carpeta de descarga: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private void BtnOpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
            OpenExternalUrl(_availableUpdate.ReleaseHtmlUrl);
    }

    private void BtnDismissUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadingUpdate)
            _updateCts?.Cancel();
        _dismissedUpdateTag = _availableUpdate?.TagName;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }
}
