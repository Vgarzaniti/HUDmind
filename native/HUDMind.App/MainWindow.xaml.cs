using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Windows;

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
    private readonly InstantSapiVoiceService _previewVoice = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private CoachOrchestrator? _orchestrator;
    private bool _shutdownComplete;
    private bool _closingInProgress;

    public MainWindow()
    {
        InitializeComponent();
        LstLiveLogs.ItemsSource = _logEntries;

        _config = _repo.LoadUserConfig();
        _accounts = _repo.LoadAccounts();
        _profiles = _repo.LoadProfiles();

        BindInitialUi();
        Closing += MainWindow_Closing;
    }

    private void BindInitialUi()
    {
        CmbGameProfile.ItemsSource = _profiles.Select(p => p.Manifest.GameId).ToList();
        if (CmbGameProfile.Items.Count > 0) CmbGameProfile.SelectedIndex = 0;

        CmbRulesGame.ItemsSource = _profiles.Select(p => p.Manifest.GameId).ToList();
        if (CmbRulesGame.Items.Count > 0) CmbRulesGame.SelectedIndex = 0;

        int screenCount = Math.Max(1, Screen.AllScreens.Length);
        CmbMonitor.ItemsSource = Enumerable.Range(1, screenCount).ToList();
        CmbMonitor.SelectedItem = Math.Clamp(_config.CaptureMonitor, 1, screenCount);

        SelectByTag(CmbCoachMode, _config.CoachMode);
        SelectByContent(CmbLanguage, _config.VoiceLanguage);
        SelectByContent(CmbEfficiency, _config.TokenEfficiencyMode);
        SelectByContent(CmbOverlayPos, _config.OverlayPosition);

        ChkEnableVoice.IsChecked = _config.EnableVoice;
        ChkEnableOverlay.IsChecked = _config.EnableOverlay;
        ChkEnableHotkeys.IsChecked = _config.EnableHotkeys;
        TxtPauseKey.Text = _config.HotkeyTogglePause;
        TxtAnalyzeKey.Text = _config.HotkeyAnalyzeNow;
        SldVolume.Value = _config.TtsVolume;
        LblVolumePct.Text = $"{(int)Math.Round(_config.TtsVolume * 100)}%";
        RefreshHotkeyButtonLabels();

        _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity, _config.CaptureMonitor);
        RefreshAccountsList();

        if (!string.IsNullOrWhiteSpace(_repo.LastLoadWarning))
            AppendLog($"⚠ AVISO: {_repo.LastLoadWarning}");

        AppendLog($"HUDMind Native (.NET 9 · 0% GPU) listo | Workspace: {_repo.WorkspaceRoot}");
        AppendLog($"Perfiles cargados: {_profiles.Count} | Cuentas IA listas: {_accounts.Count(a => a.IsReady())}");
    }

    private void RefreshHotkeyButtonLabels()
    {
        BtnAnalyzeNow.Content = $"🎯 Analizar ({_config.HotkeyAnalyzeNow})";
        bool paused = _orchestrator?.IsPaused == true;
        BtnPause.Content = paused
            ? $"▶ Reanudar ({_config.HotkeyTogglePause})"
            : $"⏸ Pausar ({_config.HotkeyTogglePause})";
    }

    private void RefreshAccountsList()
    {
        for (int i = 0; i < _accounts.Count; i++)
            _accounts[i].Priority = i + 1;
        LstAccounts.ItemsSource = _accounts.Select(a => new AccountRowView { Source = a }).ToList();
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
            if (_logEntries.Count > 0)
                LstLiveLogs.ScrollIntoView(_logEntries[^1]);
        });
    }

    private void UpdateStatsUi(CoachRuntimeStats stats)
    {
        if (_shutdownComplete) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete) return;
            StatCaptureMs.Text = stats.LastCaptureMs > 0 ? $"{stats.LastCaptureMs:F1} ms" : "— ms";
            StatAiMs.Text = stats.LastAiMs > 0 ? $"{stats.LastAiMs:F0} ms" : "— ms";
            StatVoiceMs.Text = stats.LastVoiceMs > 0 ? $"{stats.LastVoiceMs:F1} ms" : "— ms";
            StatInterventions.Text = $"{stats.Interventions} · {stats.ActiveGame}";
            HeaderStatusText.Text = stats.IsPaused
                ? $"Pausado · {stats.ActiveProvider}"
                : $"Activo · {stats.ActiveProvider}";
            PauseBadge.Visibility = stats.IsPaused ? Visibility.Visible : Visibility.Collapsed;
            RefreshHotkeyButtonLabels();
        });
    }

    private void OnPauseChanged(bool paused)
    {
        if (_shutdownComplete) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownComplete) return;
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
            _previewVoice.Dispose();
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
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_orchestrator is { IsRunning: true })
            {
                await ShutdownEngineCoreAsync(isAppClosing: false);
                return;
            }

            ApplyConfigFromUi();
            _repo.SaveUserConfig(_config);

            bool testMode = GetSelectedTag(CmbRunMode) == "test";
            string? forcedId = CmbGameProfile.SelectedItem?.ToString();

            var router = new ProviderFailoverRouter(_accounts, onLog: AppendLog);
            var windowDetector = new Win32WindowDetector();
            var capture = new FastScreenCapture();
            var voice = new InstantSapiVoiceService();

            _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity, _config.CaptureMonitor);

            _orchestrator = new CoachOrchestrator(
                _config,
                _profiles,
                router,
                windowDetector,
                capture,
                voice,
                _overlay,
                testMode: testMode,
                forcedGameId: forcedId,
                onLog: AppendLog,
                onStats: UpdateStatsUi,
                onPauseChanged: OnPauseChanged
            );

            BtnStartStop.IsEnabled = false;
            try
            {
                await _orchestrator.StartAsync();
                RegisterActiveHotkeys();

                BtnStartStop.Content = "⏹ Detener Coach";
                BtnAnalyzeNow.IsEnabled = true;
                BtnPause.IsEnabled = true;
                PauseBadge.Visibility = Visibility.Collapsed;
                RefreshHotkeyButtonLabels();
                HeaderStatusText.Text = $"Corriendo · {router.ActiveProviderLabel}";
            }
            catch (Exception ex)
            {
                AppendLog($"Error iniciando motor: {ex.Message}");
                await ShutdownEngineCoreAsync(isAppClosing: false);
            }
            finally
            {
                BtnStartStop.IsEnabled = true;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void RegisterActiveHotkeys()
    {
        _hotkeys.Stop();
        if (_config.EnableHotkeys && _orchestrator is { IsRunning: true })
        {
            _hotkeys.Start(
                _config.HotkeyTogglePause,
                _config.HotkeyAnalyzeNow,
                () => _orchestrator?.TogglePause(),
                () => _orchestrator?.TriggerOnDemandAnalysis(),
                onWarning: AppendLog
            );
        }
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

    private async Task ShutdownEngineCoreAsync(bool isAppClosing)
    {
        _hotkeys.Stop();
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync();
            _orchestrator = null;
        }
        if (!isAppClosing)
        {
            _overlay.Hide();
            BtnStartStop.Content = "▶ Iniciar Coach";
            BtnAnalyzeNow.IsEnabled = false;
            BtnPause.IsEnabled = false;
            PauseBadge.Visibility = Visibility.Collapsed;
            HeaderStatusText.Text = "Estado: Detenido";
        }
    }

    private void BtnAnalyzeNow_Click(object sender, RoutedEventArgs e) =>
        _orchestrator?.TriggerOnDemandAnalysis();

    private void BtnPause_Click(object sender, RoutedEventArgs e) =>
        _orchestrator?.TogglePause();

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e) =>
        _logEntries.Clear();

    private void CmbNewProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtNewLabel is null || TxtNewModel is null) return;
        var prov = (CmbNewProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "groq";
        (TxtNewLabel.Text, TxtNewModel.Text) = prov switch
        {
            "groq" => ("Groq LPU Gratis (~0.4s)", "meta-llama/llama-4-scout-17b-16e-instruct"),
            "gemini" => ("Gemini 2.5 Flash (~0.8s)", "gemini-2.5-flash"),
            "claude" => ("Anthropic Claude Haiku", "claude-3-5-haiku-latest"),
            "openrouter" => ("OpenRouter :free", "google/gemma-3-27b-it:free"),
            "openai" => ("OpenAI GPT-4o-mini", "gpt-4o-mini"),
            _ => ("Pollinations Gratis Sin Login", "openai-fast")
        };
    }

    private void BtnAddAccount_Click(object sender, RoutedEventArgs e)
    {
        var prov = (CmbNewProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "groq";
        var apiKey = PwdNewApiKey.Password.Trim();
        var acc = new ProviderAccount
        {
            Label = string.IsNullOrWhiteSpace(TxtNewLabel.Text) ? prov : TxtNewLabel.Text.Trim(),
            Provider = prov,
            Model = TxtNewModel.Text.Trim(),
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
    }

    private void BtnOpenGroq_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://console.groq.com/keys");
    private void BtnOpenGemini_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://aistudio.google.com/apikey");

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"No se pudo abrir navegador: {ex.Message}");
        }
    }

    private void CmbRulesGame_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var id = CmbRulesGame.SelectedItem?.ToString();
        var prof = _profiles.FirstOrDefault(p => p.Manifest.GameId == id);
        TxtCustomRules.Text = prof?.CustomRules ?? string.Empty;
    }

    private void TxtCustomRules_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LblRulesCount is not null)
            LblRulesCount.Text = $"{TxtCustomRules.Text.Length} / {ConfigRepository.MaxCustomRulesChars} caracteres (envío íntegro en tiempo real)";
    }

    private void BtnSaveRules_Click(object sender, RoutedEventArgs e)
    {
        var id = CmbRulesGame.SelectedItem?.ToString();
        var prof = _profiles.FirstOrDefault(p => p.Manifest.GameId == id);
        if (prof is null) return;
        try
        {
            _repo.SaveCustomRules(prof, TxtCustomRules.Text);
            AppendLog($"Reglas guardadas para '{prof.Manifest.DisplayName}'.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblVolumePct is null) return;
        double vol = Math.Round(SldVolume.Value, 2);
        LblVolumePct.Text = $"{(int)Math.Round(vol * 100)}%";
        _config.TtsVolume = vol;
        _orchestrator?.UpdateVoiceLive(_config.VoiceLanguage, vol);
        _previewVoice.UpdateSettings(_config.VoiceLanguage, vol);
    }

    private void ApplyConfigFromUi()
    {
        _config.CoachMode = GetSelectedTag(CmbCoachMode);
        _config.TextLanguage = (CmbLanguage.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "es";
        _config.VoiceLanguage = _config.TextLanguage;
        _config.TokenEfficiencyMode = (CmbEfficiency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "eco";
        _config.OverlayPosition = (CmbOverlayPos.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "top-right";
        _config.CaptureMonitor = CmbMonitor.SelectedItem is int m ? m : 1;
        _config.EnableVoice = ChkEnableVoice.IsChecked == true;
        _config.EnableOverlay = ChkEnableOverlay.IsChecked == true;
        _config.EnableHotkeys = ChkEnableHotkeys.IsChecked == true;
        _config.HotkeyTogglePause = string.IsNullOrWhiteSpace(TxtPauseKey.Text) ? "F9" : TxtPauseKey.Text.Trim();
        _config.HotkeyAnalyzeNow = string.IsNullOrWhiteSpace(TxtAnalyzeKey.Text) ? "F10" : TxtAnalyzeKey.Text.Trim();
        _config.TtsVolume = Math.Round(SldVolume.Value, 2);
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        ApplyConfigFromUi();
        _repo.SaveUserConfig(_config);
        _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity, _config.CaptureMonitor);
        _orchestrator?.UpdateVoiceLive(_config.VoiceLanguage, _config.TtsVolume);
        _previewVoice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume);
        RefreshHotkeyButtonLabels();
        RegisterActiveHotkeys();
        AppendLog("Preferencias guardadas y aplicadas en vivo (preservando campos Python).");
    }

    private void BtnPreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        ApplyConfigFromUi();
        _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity, _config.CaptureMonitor);
        _overlay.ShowCallout("Rotá segundo palo, tenés 18 de boost.", UrgencyLevel.High, "Rocket League", 4.0);
        if (_config.EnableVoice)
        {
            string lang = _config.VoiceLanguage;
            double vol = _config.TtsVolume;
            _ = Task.Run(() =>
            {
                _previewVoice.WarmUp(lang, vol);
                _previewVoice.UpdateSettings(lang, vol);
                double ms = _previewVoice.SpeakInstant("Rotá segundo palo, tenés dieciocho de boost.");
                AppendLog($"Preview Voz SAPI5 disparado en {ms:F1} ms (sin cortar audio).");
            });
        }
    }

    private static string GetSelectedTag(System.Windows.Controls.ComboBox cb) =>
        (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

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
