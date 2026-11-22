using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Windows;
using MediaColor = System.Windows.Media.Color;

namespace HUDMind.App;

public sealed class AccountRowView
{
    public required ProviderAccount Source { get; init; }
    public int Priority => Source.Priority;
    public string Label => Source.Label;
    public string Provider => Source.Provider;
    public string Model => Source.Model;
    public string MaskedKeyDisplay => Source.MaskedKey();
    public string EnabledDisplay => Source.Enabled ? "Sí" : "No";
}

public partial class MainWindow : Window
{
    private const int MaxLogEntries = 180;

    private readonly ConfigRepository _repo = new();
    private UserConfig _config;
    private List<ProviderAccount> _accounts;
    private List<GameProfile> _profiles;

    private readonly ObservableCollection<string> _logEntries = [];
    private readonly OverlayWindow _overlay = new();
    private readonly Win32GlobalHotkeyManager _hotkeys = new();
    private readonly LoopbackGsiService _gsiService = new();
    private readonly HybridVoiceService _sharedVoice;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Win32TrayIcon? _trayIcon;

    private CoachOrchestrator? _orchestrator;
    private RealtimeLiveCoachOrchestrator? _liveOrchestrator;
    private string _lastMatchGameName = string.Empty;
    private string _lastMatchDigest = string.Empty;
    private bool _shutdownComplete;
    private bool _closingInProgress;
    private bool _syncingUi;
    private bool _chatSending;
    private bool _rulesDirty;
    private bool _onboardingDismissed;
    private string? _lastRulesGameId;

    private bool IsAnyEngineRunning =>
        (_orchestrator is { IsRunning: true }) || (_liveOrchestrator is { IsRunning: true });

    private bool IsAnyEnginePaused =>
        (_orchestrator?.IsPaused == true) || (_liveOrchestrator?.IsPaused == true);

    public MainWindow()
    {
        InitializeComponent();
        LstLiveLogs.ItemsSource = _logEntries;

        _sharedVoice = new HybridVoiceService(_repo.WorkspaceRoot);
        _config = _repo.LoadUserConfig();
        _sharedVoice.SetSpeechRate(_config.TtsRate);
        _accounts = _repo.LoadAccounts();
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
            onTogglePause: () =>
            {
                _orchestrator?.TogglePause();
                _liveOrchestrator?.TogglePause();
            },
            onExit: () => Dispatcher.BeginInvoke(Close),
            isPaused: () => IsAnyEnginePaused,
            isRunning: () => IsAnyEngineRunning);
        _trayIcon.Attach(this);
        if (_config.MinimizeToTray && WindowState == WindowState.Minimized)
        {
            Hide();
            _trayIcon.Show();
        }
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

    private void BindInitialUi()
    {
        CmbGameProfile.ItemsSource = _profiles.Select(p => p.Manifest.GameId).ToList();
        if (CmbGameProfile.Items.Count > 0) CmbGameProfile.SelectedIndex = 0;

        CmbRulesGame.ItemsSource = _profiles.Select(p => p.Manifest.GameId).ToList();
        if (CmbRulesGame.Items.Count > 0) CmbRulesGame.SelectedIndex = 0;

        RefreshMonitorsList();
        RefreshAudioDevicesList();

        _syncingUi = true;
        try
        {
            SelectByTag(CmbCoachMode, _config.CoachMode);
            SelectByTag(CmbSkillTier, _config.PlayerSkillTier);
            TxtRankLabel.Text = _config.PlayerRankLabel;
            SelectByTag(CmbCoachStyle, _config.CoachStyle);
            SelectByTag(CmbLiveAutoRate, ((int)Math.Round(_config.LiveAutoCoachSeconds)).ToString());
            SelectByTag(CmbVoiceGender, _config.VoiceGender);
            SelectByTag(CmbQuickGender, _config.VoiceGender);
            SelectByTag(CmbVoiceStyle, _config.VoiceStyle);
            SelectByTag(CmbEngineType, _config.UseLiveConversational ? "live" : "classic");
            SelectByTag(CmbCommMode, _config.CommunicationMode);
            SelectByTag(CmbCommModeSettings, _config.CommunicationMode);
            SelectByTag(CmbMicInputMode, _config.IsPushToTalkMode ? "push_to_talk" : "open_mic");
            TxtPttKey.Text = string.IsNullOrWhiteSpace(_config.PushToTalkKey) ? "V" : _config.PushToTalkKey;
            SelectByTag(CmbMicDevice, _config.AudioInputDeviceName);
            SelectByTag(CmbSpeakerDevice, _config.AudioOutputDeviceName);
            SelectByContent(CmbLanguage, _config.VoiceLanguage);
            SelectByContent(CmbEfficiency, _config.TokenEfficiencyMode);
            SelectByContent(CmbOverlayPos, _config.OverlayPosition);
            SldOverlayScale.Value = Math.Clamp(_config.OverlayScale, 0.75, 1.45);
            LblOverlayScale.Text = $"{(int)Math.Round(SldOverlayScale.Value * 100)}%";
            SldOverlayOpacity.Value = Math.Clamp(_config.OverlayOpacity, 0.45, 1.0);
            LblOverlayOpacity.Text = $"{(int)Math.Round(SldOverlayOpacity.Value * 100)}%";
            SldVolume.Value = _config.TtsVolume;
            LblVolumePct.Text = $"{(int)Math.Round(_config.TtsVolume * 100)}%";
        }
        finally
        {
            _syncingUi = false;
        }

        ChkEnableOverlay.IsChecked = _config.EnableOverlay;
        ChkOverlayCompact.IsChecked = _config.OverlayCompact;
        ChkEnableHotkeys.IsChecked = _config.EnableHotkeys;
        ChkMinimizeToTray.IsChecked = _config.MinimizeToTray;
        TxtPauseKey.Text = _config.HotkeyTogglePause;
        TxtAnalyzeKey.Text = _config.HotkeyAnalyzeNow;
        TxtMicKey.Text = _config.HotkeyToggleMic;
        RefreshHotkeyButtonLabels();

        ConfigureOverlayFromCurrentConfig();
        RefreshAccountsList();
        RefreshOnboardingBanner();
        UpdateLatencyCardHeaders(isLive: false);

        if (!string.IsNullOrWhiteSpace(_repo.LastLoadWarning))
            AppendLog($"⚠ AVISO: {_repo.LastLoadWarning}");

        AppendLog($"HUDMind Native (.NET 9 · 0% GPU) listo | Workspace: {_repo.WorkspaceRoot}");
        AppendLog($"Perfiles cargados: {_profiles.Count} | Cuentas IA listas: {_accounts.Count(a => a.IsReady())}");
        if (_config.VoiceStyle == "neural" && !_sharedVoice.IsNeuralWorkerPresent)
            AppendLog("ℹ️ Modo standalone: usando voces Windows OneCore instantáneas (~8ms, 0 internet requerido).");
    }

    private void RefreshMonitorsList()
    {
        int screenCount = Math.Max(1, Win32DisplayHelper.GetAllWorkingAreas().Length);
        CmbMonitor.ItemsSource = Enumerable.Range(1, screenCount).ToList();
        CmbMonitor.SelectedItem = Math.Clamp(_config.CaptureMonitor, 1, screenCount);
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

    private void ConfigureOverlayFromCurrentConfig() =>
        _overlay.Configure(
            _config.OverlayPosition,
            _config.OverlayWidth,
            _config.OverlayOpacity,
            _config.CaptureMonitor,
            _config.OverlayScale,
            _config.OverlayCompact);

    private void RefreshOnboardingBanner()
    {
        if (OnboardingBanner is null) return;
        bool hasUserApiKeyAccount = _accounts.Any(a =>
            a.Enabled && !a.IsKeylessAllowed && !string.IsNullOrWhiteSpace(a.ApiKey));
        OnboardingBanner.Visibility = (!hasUserApiKeyAccount && !_onboardingDismissed) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshMatchReviewButtonState()
    {
        if (BtnMatchReview is null) return;
        bool hasLiveEvents = _liveOrchestrator?.GetMatchSessionDigest().Count > 0;
        bool hasClassicEvents = _orchestrator?.GetMatchSessionDigest().Count > 0;
        bool hasSaved = !string.IsNullOrWhiteSpace(_lastMatchDigest);
        BtnMatchReview.IsEnabled = (hasLiveEvents || hasClassicEvents || hasSaved) && _accounts.Any(a => a.IsReady());
    }

    private void RefreshHotkeyButtonLabels()
    {
        BtnAnalyzeNow.Content = $"🎯 Analizar ({_config.HotkeyAnalyzeNow})";
        bool paused = IsAnyEnginePaused;
        BtnPause.Content = paused
            ? $"▶ Reanudar ({_config.HotkeyTogglePause})"
            : $"⏸ Pausar ({_config.HotkeyTogglePause})";
    }

    private void UpdateLatencyCardHeaders(bool isLive)
    {
        if (isLive)
        {
            LblAiCardTitle.Text = "PRIMER AUDIO / TTFA (WS)";
            LblVoiceCardTitle.Text = "CANAL DE VOZ NATIVA";
        }
        else
        {
            LblAiCardTitle.Text = "VISIÓN / SENSOR IA";
            LblVoiceCardTitle.Text = "VOZ DEL COACH";
        }
    }

    private void ResetStatsCards()
    {
        StatCaptureMs.Text = "— ms";
        StatAiMs.Text = "— ms";
        StatVoiceMs.Text = "— ms";
        StatInterventions.Text = "0";
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
        for (int i = 0; i < _accounts.Count; i++)
            _accounts[i].Priority = i + 1;
        LstAccounts.ItemsSource = _accounts.Select(a => new AccountRowView { Source = a }).ToList();
        RefreshOnboardingBanner();
        RefreshMatchReviewButtonState();
    }

    private void AppendLog(string message)
    {
        if (_shutdownComplete) return;
        var safeMsg = SecretRedactor.Redact(message, _accounts.Select(a => a.ApiKey));
        var line = $"[{DateTime.Now:HH:mm:ss}] {safeMsg}";
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete) return;
            _logEntries.Add(line);
            while (_logEntries.Count > MaxLogEntries)
                _logEntries.RemoveAt(0);
            if (_logEntries.Count > 0 && WindowState != WindowState.Minimized)
                LstLiveLogs.ScrollIntoView(_logEntries[^1]);
        });
    }

    private void UpdateStatsUi(CoachRuntimeStats stats)
    {
        if (_shutdownComplete) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete || !IsAnyEngineRunning) return;
            StatCaptureMs.Text = stats.LastCaptureMs > 0 ? $"{stats.LastCaptureMs:F1} ms" : "— ms";
            StatAiMs.Text = stats.LastAiMs > 0 ? $"{stats.LastAiMs:F0} ms" : "— ms";
            StatVoiceMs.Text = _liveOrchestrator is { IsRunning: true }
                ? (_config.IsTextOnlyMode ? "Silencio" : "Integrada (WS)")
                : (stats.LastVoiceMs > 0 ? $"{stats.LastVoiceMs:F1} ms" : "— ms");
            StatInterventions.Text = $"{stats.Interventions} · {stats.ActiveGame}";
            HeaderStatusText.Text = stats.IsPaused
                ? $"Pausado · {stats.ActiveProvider}"
                : $"Activo · {stats.ActiveProvider}";
            PauseBadge.Visibility = stats.IsPaused ? Visibility.Visible : Visibility.Collapsed;
            _trayIcon?.UpdateTooltip($"HUDMind · {HeaderStatusText.Text}");
            RefreshMatchReviewButtonState();
            RefreshHotkeyButtonLabels();
        });
    }

    private void OnPauseChanged(bool paused)
    {
        if (_shutdownComplete) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete || !IsAnyEngineRunning) return;
            PauseBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
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
            await ShutdownEngineAsync(isAppClosing: true);
            _gsiService.Dispose();
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
        BtnStartStop.IsEnabled = false;
        await _lifecycleLock.WaitAsync();
        try
        {
            if (IsAnyEngineRunning)
            {
                await ShutdownEngineCoreAsync(isAppClosing: false);
                return;
            }

            ApplyConfigFromUi();
            _repo.SaveUserConfig(_config);

            bool testMode = GetSelectedTag(CmbRunMode) == "test";
            string? forcedId = CmbGameProfile.SelectedItem?.ToString();
            bool wantLive = GetSelectedTag(CmbEngineType) == "live";
            var liveBackend = wantLive ? LiveBackendFactory.TryCreateFromAccounts(_accounts) : null;

            ConfigureOverlayFromCurrentConfig();
            if (_gsiService.TryStart())
                AppendLog($"📡 Receptor Valve GSI oficial activo (127.0.0.1:{LoopbackGsiService.DefaultPort}) para CS2 / Dota 2.");

            try
            {
                if (liveBackend is not null)
                {
                    var sensor = new TextOnlySensorService(_gsiService);
                    if (!sensor.HasOcrEngine)
                        AppendLog("ℹ️ Windows OCR sin packs de idioma instalados (filtro local de menús por OCR desactivado).");

                    _liveOrchestrator = new RealtimeLiveCoachOrchestrator(
                        liveBackend,
                        _config,
                        _profiles,
                        new Win32WindowDetector(),
                        new FastScreenCapture(),
                        new Win32StreamingPcmPlayer(),
                        new Win32StreamingMicCapture(),
                        _overlay,
                        testMode: testMode,
                        forcedGameId: forcedId,
                        onLog: AppendLog,
                        onStats: UpdateStatsUi,
                        onPauseChanged: OnPauseChanged,
                        localPhaseGate: sensor,
                        isPttKeyPressed: () => Win32KeyStateProbe.IsPushToTalkKeyDown(_config.PushToTalkKey)
                    );
                    await _liveOrchestrator.StartAsync();
                    UpdateLatencyCardHeaders(isLive: true);
                    TxtHeaderBadge.Text = ".NET 9 · 0% GPU · LIVE WS";
                    HeaderStatusText.Text = _config.IsTextOnlyMode
                        ? $"💬 {liveBackend.ProviderName} · Solo texto"
                        : _config.IsCoachVoiceOnlyMode
                            ? $"🔊 {liveBackend.ProviderName} · Solo voz ({_liveOrchestrator.ActiveVoiceLabel})"
                            : $"🎙 {liveBackend.ProviderName} · Charla ({_liveOrchestrator.ActiveVoiceLabel})";
                }
                else
                {
                    if (wantLive)
                    {
                        AppendLog("ℹ️ Tu cuenta prioritaria usa HTTP REST Multi-IA (sin WebSocket Live) — cambiando UI a Turnos Multi-IA.");
                        _syncingUi = true;
                        try { SelectByTag(CmbEngineType, "classic"); }
                        finally { _syncingUi = false; }
                    }

                    var textOnlySensor = new TextOnlySensorService(_gsiService);
                    if (_accounts.Any(a => a.IsReady() && !a.SupportsVision) && !textOnlySensor.HasOcrEngine)
                        AppendLog("⚠ Tu cuenta prioritaria es ciego-texto pero falta el pack OCR de Windows (solo usarán logs si existen).");

                    var router = new ProviderFailoverRouter(_accounts, onLog: AppendLog, textOnlySensor: textOnlySensor);
                    _orchestrator = new CoachOrchestrator(
                        _config,
                        _profiles,
                        router,
                        new Win32WindowDetector(),
                        new FastScreenCapture(),
                        _sharedVoice,
                        _overlay,
                        testMode: testMode,
                        forcedGameId: forcedId,
                        onLog: AppendLog,
                        onStats: UpdateStatsUi,
                        onPauseChanged: OnPauseChanged,
                        disposeVoice: false
                    );
                    await _orchestrator.StartAsync();
                    UpdateLatencyCardHeaders(isLive: false);
                    TxtHeaderBadge.Text = ".NET 9 · 0% GPU · HTTP/2 MULTI-IA";
                    HeaderStatusText.Text = $"Corriendo · {router.ActiveProviderLabel}";
                }

                RegisterActiveHotkeys();
                BtnStartStop.Content = "⏹ Detener Coach";
                SetRunningControlLocks(true);
                PauseBadge.Visibility = Visibility.Collapsed;
                RefreshHotkeyButtonLabels();
                _trayIcon?.UpdateTooltip($"HUDMind · {HeaderStatusText.Text}");
            }
            catch (Exception ex)
            {
                AppendLog($"Error iniciando motor: {SecretRedactor.Redact(ex.Message, _accounts.Select(a => a.ApiKey))}");
                await ShutdownEngineCoreAsync(isAppClosing: false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
            BtnStartStop.IsEnabled = true;
        }
    }

    private void RegisterActiveHotkeys()
    {
        _hotkeys.Stop();
        if (_config.EnableHotkeys && IsAnyEngineRunning)
        {
            var (pOk, aOk, mOk) = _hotkeys.Start(
                _config.HotkeyTogglePause,
                _config.HotkeyAnalyzeNow,
                _config.HotkeyToggleMic,
                () =>
                {
                    _orchestrator?.TogglePause();
                    _liveOrchestrator?.TogglePause();
                },
                () =>
                {
                    _orchestrator?.TriggerOnDemandAnalysis();
                    _liveOrchestrator?.TriggerOnDemandAnalysis();
                },
                onToggleMic: ToggleMicViaHotkey,
                onWarning: AppendLog
            );
            if (!pOk || !aOk || !mOk)
                AppendLog($"ℹ️ Atajos activos — Pausa:{(pOk ? "OK" : "OCUPADA")} | Analizar:{(aOk ? "OK" : "OCUPADA")} | Mic:{(mOk ? "OK" : "OCUPADA")}");
        }
    }

    private void ToggleMicViaHotkey()
    {
        if (_liveOrchestrator is not { IsRunning: true }) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete || _liveOrchestrator is not { IsRunning: true }) return;
            string nextMode = _config.IsMicActive ? "coach_voice" : "conversation";
            ApplyCommunicationModeFromUi(nextMode, fromQuickBar: false);
            if (_config.EnableOverlay)
            {
                _overlay.ShowCallout(
                    _config.IsMicActive ? "🎤 Micrófono ACTIVO" : "🔇 Micrófono SILENCIADO",
                    UrgencyLevel.Low,
                    "HUDMind Live",
                    2.0);
            }
        });
    }

    private async Task ShutdownEngineAsync(bool isAppClosing)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await ShutdownEngineCoreAsync(isAppClosing);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void SnapshotLastMatchSession()
    {
        bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        if (_liveOrchestrator is not null)
        {
            var (g, d, count) = _liveOrchestrator.GetMatchSessionDigest(isEn);
            if (count > 0 && !string.IsNullOrWhiteSpace(d))
            {
                _lastMatchGameName = g;
                _lastMatchDigest = d;
            }
        }
        else if (_orchestrator is not null)
        {
            var (g, d, count) = _orchestrator.GetMatchSessionDigest(isEn);
            if (count > 0 && !string.IsNullOrWhiteSpace(d))
            {
                _lastMatchGameName = g;
                _lastMatchDigest = d;
            }
        }
    }

    private async Task ShutdownEngineCoreAsync(bool isAppClosing)
    {
        _hotkeys.Stop();
        SnapshotLastMatchSession();
        if (_liveOrchestrator is not null)
        {
            await _liveOrchestrator.DisposeAsync();
            _liveOrchestrator = null;
        }
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync();
            _orchestrator = null;
        }
        if (!isAppClosing)
        {
            _overlay.HideImmediately();
            BtnStartStop.Content = "▶ Iniciar Coach";
            SetRunningControlLocks(false);
            ResetStatsCards();
            RefreshMatchReviewButtonState();
            PauseBadge.Visibility = Visibility.Collapsed;
            TxtHeaderBadge.Text = ".NET 9 · 0% GPU · $0 COSTO · MULTI-IA";
            HeaderStatusText.Text = "Estado: Detenido";
            _trayIcon?.UpdateTooltip("HUDMind · Detenido");
        }
    }

    private void BtnAnalyzeNow_Click(object sender, RoutedEventArgs e)
    {
        _orchestrator?.TriggerOnDemandAnalysis();
        _liveOrchestrator?.TriggerOnDemandAnalysis();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        _orchestrator?.TogglePause();
        _liveOrchestrator?.TogglePause();
    }

    private void CmbCommMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbCommModeSettings is null) return;
        ApplyCommunicationModeFromUi(GetSelectedTag(CmbCommMode), fromQuickBar: true);
    }

    private void CmbCommModeSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbCommMode is null) return;
        ApplyCommunicationModeFromUi(GetSelectedTag(CmbCommModeSettings), fromQuickBar: false);
    }

    private void ApplyCommunicationModeFromUi(string mode, bool fromQuickBar)
    {
        _config.SetCommunicationMode(mode);
        _syncingUi = true;
        try
        {
            SelectByTag(CmbCommMode, _config.CommunicationMode);
            SelectByTag(CmbCommModeSettings, _config.CommunicationMode);
        }
        finally
        {
            _syncingUi = false;
        }

        _repo.SaveUserConfig(_config);
        _liveOrchestrator?.SetCommunicationMode(_config.CommunicationMode);
        _orchestrator?.SetCommunicationMode(_config.CommunicationMode);

        if (!IsAnyEngineRunning)
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
        if (_chatSending || !IsAnyEngineRunning) return;
        var text = (TxtChatInput.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return;

        _chatSending = true;
        BtnSendChat.IsEnabled = false;
        try
        {
            bool sent = false;
            if (_liveOrchestrator is { IsRunning: true })
                sent = await _liveOrchestrator.SendUserTextAsync(text, attachCurrentScreen: true);
            else if (_orchestrator is { IsRunning: true })
                sent = await _orchestrator.SendUserTextAsync(text);

            // Preserve user's draft if send failed or WebSocket wasn't connected yet!
            if (sent)
                TxtChatInput.Clear();
        }
        catch (Exception ex)
        {
            AppendLog($"Error enviando chat: {SecretRedactor.Redact(ex.Message, _accounts.Select(a => a.ApiKey))}");
        }
        finally
        {
            _chatSending = false;
            BtnSendChat.IsEnabled = IsAnyEngineRunning;
            if (IsAnyEngineRunning)
                TxtChatInput.Focus();
        }
    }

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
        if (TxtNewLabel is null || TxtNewModel is null) return;
        var prov = (CmbNewProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "groq";
        (TxtNewLabel.Text, TxtNewModel.Text) = prov switch
        {
            "groq" => ("Groq LPU Gratis (~0.4s)", "meta-llama/llama-4-scout-17b-16e-instruct"),
            "gemini" => ("Gemini 2.5 Flash (~0.8s)", "gemini-2.5-flash"),
            "openai" => ("OpenAI GPT-4o-mini / Realtime", "gpt-4o-mini"),
            "xai" => ("xAI Grok", "grok-2-vision-1212"),
            "claude" => ("Anthropic Claude Haiku", "claude-3-5-haiku-latest"),
            "openrouter" => ("OpenRouter :free", "google/gemma-3-27b-it:free"),
            "mistral" => ("Mistral Pixtral", "pixtral-12b-2409"),
            "airforce" => ("Airforce Gratis Sin Login", "open-mistral-nemo"),
            "ollama" => ("Ollama Local", "llama3.2-vision"),
            _ => ("Pollinations Gratis Sin Login", "openai-fast")
        };
    }

    private void BtnAddAccount_Click(object sender, RoutedEventArgs e)
    {
        var prov = (CmbNewProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "groq";
        var model = TxtNewModel.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            System.Windows.MessageBox.Show("Ingresá un nombre de modelo válido.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var apiKey = PwdNewApiKey.Password.Trim();
        var acc = new ProviderAccount
        {
            Label = string.IsNullOrWhiteSpace(TxtNewLabel.Text) ? prov : TxtNewLabel.Text.Trim(),
            Provider = prov,
            Model = model,
            ApiKey = apiKey,
            Priority = 1,
            Enabled = true
        };
        if (!acc.IsReady())
        {
            System.Windows.MessageBox.Show("Pegá tu API Key para este proveedor.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _accounts.Insert(0, acc);
        PwdNewApiKey.Clear();
        RefreshAccountsList();
        _repo.SaveAccounts(_accounts);
        AppendLog($"Cuenta agregada en prioridad #1: {acc.Label} [{acc.Provider}:{acc.Model}]");
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedAccount(-1);
    private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedAccount(1);

    private void MoveSelectedAccount(int delta)
    {
        int idx = LstAccounts.SelectedIndex;
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= _accounts.Count) return;
        (_accounts[idx], _accounts[target]) = (_accounts[target], _accounts[idx]);
        RefreshAccountsList();
        LstAccounts.SelectedIndex = target;
        _repo.SaveAccounts(_accounts);
    }

    private void BtnToggleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (LstAccounts.SelectedItem is not AccountRowView row) return;
        row.Source.Enabled = !row.Source.Enabled;
        RefreshAccountsList();
        _repo.SaveAccounts(_accounts);
    }

    private void BtnDeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstAccounts.SelectedIndex;
        if (idx < 0) return;
        _accounts.RemoveAt(idx);
        RefreshAccountsList();
        _repo.SaveAccounts(_accounts);
    }

    private async void BtnTestAccount_Click(object sender, RoutedEventArgs e)
    {
        if (LstAccounts.SelectedItem is not AccountRowView row) return;
        BtnTestAccount.IsEnabled = false;
        try
        {
            await using var adapter = ProviderFailoverRouter.CreateDefaultAdapter(row.Source);
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
        var nextId = CmbRulesGame.SelectedItem?.ToString();
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
                try { CmbRulesGame.SelectedItem = _lastRulesGameId; }
                finally { _syncingUi = false; }
                return;
            }
        }

        _lastRulesGameId = nextId;
        var prof = _profiles.FirstOrDefault(p => p.Manifest.GameId == nextId);
        _syncingUi = true;
        try { TxtCustomRules.Text = prof?.CustomRules ?? string.Empty; }
        finally { _syncingUi = false; }
        _rulesDirty = false;
        UpdateRulesCounterUi();
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
        var id = CmbRulesGame.SelectedItem?.ToString();
        var prof = _profiles.FirstOrDefault(p => p.Manifest.GameId == id);
        if (prof is null) return;
        try
        {
            _repo.SaveCustomRules(prof, TxtCustomRules.Text);
            _rulesDirty = false;
            _liveOrchestrator?.RefreshLivePersona($"reglas {prof.Manifest.DisplayName}");
            AppendLog($"Reglas guardadas y aplicadas para '{prof.Manifest.DisplayName}'.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _orchestrator?.UpdateVoiceLive(_config.VoiceLanguage, _config.TtsVolume, gender, style);
        _liveOrchestrator?.SetVoiceGenderLive(gender);
        AppendLog($"Voz actualizada: {(gender == "female" ? "👩 Mujer" : "👨 Hombre")} ({(_liveOrchestrator is { IsRunning: true } ? _liveOrchestrator.ActiveVoiceLabel : style)}).");
    }

    private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || LblVolumePct is null) return;
        double vol = Math.Round(SldVolume.Value, 2);
        LblVolumePct.Text = $"{(int)Math.Round(vol * 100)}%";
        _config.TtsVolume = vol;
        _sharedVoice.UpdateSettings(_config.VoiceLanguage, vol, _config.VoiceGender, _config.VoiceStyle);
        _orchestrator?.UpdateVoiceLive(_config.VoiceLanguage, vol, _config.VoiceGender, _config.VoiceStyle);
        _liveOrchestrator?.SetVolume(vol);
    }

    private void SldOverlayVisual_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || LblOverlayScale is null || LblOverlayOpacity is null) return;
        _config.OverlayScale = Math.Round(SldOverlayScale.Value, 2);
        _config.OverlayOpacity = Math.Round(SldOverlayOpacity.Value, 2);
        LblOverlayScale.Text = $"{(int)Math.Round(_config.OverlayScale * 100)}%";
        LblOverlayOpacity.Text = $"{(int)Math.Round(_config.OverlayOpacity * 100)}%";
        ConfigureOverlayFromCurrentConfig();
    }

    private void BtnGoAccounts_Click(object sender, RoutedEventArgs e) =>
        MainTabs.SelectedIndex = 1;

    private void BtnGoSettings_Click(object sender, RoutedEventArgs e) =>
        MainTabs.SelectedIndex = 3;

    private void BtnDismissOnboarding_Click(object sender, RoutedEventArgs e)
    {
        _onboardingDismissed = true;
        RefreshOnboardingBanner();
    }

    private async void BtnMatchReview_Click(object sender, RoutedEventArgs e)
    {
        SnapshotLastMatchSession();
        if (string.IsNullOrWhiteSpace(_lastMatchDigest))
        {
            AppendLog("ℹ️ Todavía no hay consejos registrados en esta sesión para resumir.");
            return;
        }
        if (!_accounts.Any(a => a.IsReady()))
        {
            AppendLog("⚠ Agregá una cuenta IA habilitada para generar el resumen post-partida.");
            return;
        }

        BtnMatchReview.IsEnabled = false;
        try
        {
            AppendLog($"📊 Analizando partida reciente ({_lastMatchGameName})...");
            await using var router = new ProviderFailoverRouter(_accounts, onLog: AppendLog);
            var (review, ms, model) = await router.GenerateMatchReviewAsync(_lastMatchGameName, _lastMatchDigest, _config);
            if (string.IsNullOrWhiteSpace(review))
            {
                AppendLog($"No se pudo generar el resumen post-partida [{model}].");
                return;
            }

            AppendLog($"📊 === RESUMEN POST-PARTIDA ({_lastMatchGameName}, {ms:F0}ms · {model}) ===");
            foreach (var rawLine in review.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                AppendLog($"   {rawLine.Trim()}");

            if (_config.IsCoachVoiceActive)
            {
                string spoken = review.Replace("*", "").Replace("#", "").Trim();
                if (spoken.Length > 280) spoken = spoken[..280];
                string lang = _config.VoiceLanguage;
                double vol = _config.TtsVolume;
                string gender = _config.VoiceGender;
                string style = _config.VoiceStyle;
                _ = Task.Run(() =>
                {
                    _sharedVoice.WarmUp(lang, vol, gender, style);
                    _sharedVoice.UpdateSettings(lang, vol, gender, style);
                    _sharedVoice.SpeakInstant(spoken);
                });
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Error generando resumen post-partida: {SecretRedactor.Redact(ex.Message, _accounts.Select(a => a.ApiKey))}");
        }
        finally
        {
            RefreshMatchReviewButtonState();
        }
    }

    private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyEngineRunning)
        {
            System.Windows.MessageBox.Show("Detené primero el Coach Live para hacer una prueba aislada de micrófono.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedMic = GetSelectedTag(CmbMicDevice, string.Empty);
        BtnTestMic.IsEnabled = false;
        AppendLog($"🎤 Probando micrófono '{(string.IsNullOrEmpty(selectedMic) ? "Predeterminado" : selectedMic)}' por 1 segundo (hablá ahora)...");

        try
        {
            double peakRms = 0;
            int chunks = 0;
            string? errSeen = null;

            using (var testMic = new Win32StreamingMicCapture())
            {
                testMic.SetDevice(selectedMic);
                bool started = testMic.TryStart(
                    chunk =>
                    {
                        Interlocked.Increment(ref chunks);
                        double rms = ComputeChunkRms16(chunk);
                        double cur = Volatile.Read(ref peakRms);
                        if (rms > cur) Volatile.Write(ref peakRms, rms);
                    },
                    err => errSeen = err);

                if (!started)
                {
                    var errText = errSeen ?? "No se pudo abrir el micrófono seleccionado.";
                    AppendLog($"⚠ {errText}");
                    System.Windows.MessageBox.Show(errText, "Prueba de Micrófono", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await Task.Delay(1000);
                testMic.Stop();
            }

            string result = peakRms >= 220
                ? $"✅ Micrófono funcionando perfecto ({chunks} tramas, pico RMS {peakRms:F0})."
                : $"ℹ️ Micrófono conectado ({chunks} tramas), pero nivel muy bajo (RMS {peakRms:F0}). Probá acercarte o elegir otro dispositivo.";
            AppendLog(result);
            System.Windows.MessageBox.Show(result, "Prueba de Micrófono", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            BtnTestMic.IsEnabled = true;
        }
    }

    private static double ComputeChunkRms16(byte[] pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            sumSq += s * (double)s;
        }
        return Math.Sqrt(sumSq / samples);
    }

    private void BtnInstallGsi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (count, msg) = LoopbackGsiService.InstallValveGsiConfigs();
            AppendLog($"🎮 GSI Valve: {msg}");
            if (count > 0)
                _gsiService.TryStart();
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
        _config.LiveAutoCoachSeconds = double.TryParse(GetSelectedTag(CmbLiveAutoRate), out double autoSec) ? autoSec : 12.0;
        _config.VoiceGender = GetSelectedTag(CmbVoiceGender);
        _config.VoiceStyle = GetSelectedTag(CmbVoiceStyle);
        _config.UseLiveConversational = GetSelectedTag(CmbEngineType) == "live";
        _config.SetCommunicationMode(GetSelectedTag(CmbCommMode));
        _config.MicInputMode = GetSelectedTag(CmbMicInputMode, "open_mic");
        if (HotkeyParser.TryParsePushToTalkKey(TxtPttKey.Text, out _, out var pttSafe)) _config.PushToTalkKey = pttSafe;
        _config.AudioInputDeviceName = GetSelectedTag(CmbMicDevice, string.Empty);
        _config.AudioOutputDeviceName = GetSelectedTag(CmbSpeakerDevice, string.Empty);
        _config.TextLanguage = (CmbLanguage.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "es";
        _config.VoiceLanguage = _config.TextLanguage;
        _config.TokenEfficiencyMode = (CmbEfficiency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "eco";
        _config.OverlayPosition = (CmbOverlayPos.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "top-right";
        _config.CaptureMonitor = CmbMonitor.SelectedItem is int m ? m : 1;
        _config.EnableOverlay = ChkEnableOverlay.IsChecked == true;
        _config.OverlayCompact = ChkOverlayCompact.IsChecked == true;
        _config.EnableHotkeys = ChkEnableHotkeys.IsChecked == true;
        _config.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
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
        ConfigureOverlayFromCurrentConfig();
        if (!_config.EnableOverlay)
            _overlay.HideImmediately();

        _sharedVoice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
        _orchestrator?.UpdateVoiceLive(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
        _liveOrchestrator?.SetVolume(_config.TtsVolume);
        _liveOrchestrator?.SetAudioDevicesLive(_config.AudioInputDeviceName, _config.AudioOutputDeviceName);
        _liveOrchestrator?.RefreshLivePersona($"{_config.PlayerSkillTier} · {_config.CoachStyle}");
        RefreshHotkeyButtonLabels();
        RegisterActiveHotkeys();
        AppendLog($"Preferencias guardadas y aplicadas en vivo (Nivel: {_config.PlayerSkillTier}, Mic: {(_config.IsPushToTalkMode ? $"PTT {_config.PushToTalkKey}" : "Abierto")}).");
    }

    private void BtnPreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyConfigFromUi()) return;
        ConfigureOverlayFromCurrentConfig();
        if (_config.EnableOverlay)
            _overlay.ShowCallout("Rotá segundo palo, tenés 18 de boost.", UrgencyLevel.High, "Rocket League", 4.0);

        if (!_config.IsCoachVoiceActive)
        {
            AppendLog("Preview mostrado solo en overlay (estás en modo 'Solo texto').");
            return;
        }

        string lang = _config.VoiceLanguage;
        string gender = _config.VoiceGender;
        string style = _config.VoiceStyle;
        double vol = _config.TtsVolume;
        _ = Task.Run(() =>
        {
            _sharedVoice.WarmUp(lang, vol, gender, style);
            _sharedVoice.UpdateSettings(lang, vol, gender, style);
            double ms = _sharedVoice.SpeakInstant("Rotá segundo palo, tenés dieciocho de boost.");
            AppendLog($"Preview Voz [{_sharedVoice.EngineName}] disparado en {ms:F1} ms.");
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

    private static void SelectByContent(System.Windows.Controls.ComboBox cb, string content)
    {
        foreach (ComboBoxItem item in cb.Items)
        {
            if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                cb.SelectedItem = item;
                return;
            }
        }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }
}
