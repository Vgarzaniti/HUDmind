using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ConfigRepository _repo = new();
    private UserConfig _config;
    private List<ProviderAccount> _accounts;
    private List<GameProfile> _profiles;

    private readonly OverlayWindow _overlay = new();
    private readonly Win32GlobalHotkeyManager _hotkeys = new();
    private CoachOrchestrator? _orchestrator;

    public MainWindow()
    {
        InitializeComponent();
        _config = _repo.LoadUserConfig();
        _accounts = _repo.LoadAccounts();
        _profiles = _repo.LoadProfiles();

        BindInitialUi();
        Closing += async (_, _) => await ShutdownEngineAsync();
    }

    private void BindInitialUi()
    {
        CmbGameProfile.ItemsSource = _profiles.Select(p => p.Manifest.GameId).ToList();
        if (CmbGameProfile.Items.Count > 0)
            CmbGameProfile.SelectedIndex = 0;

        CmbRulesGame.ItemsSource = _profiles.Select(p => p.Manifest.GameId).ToList();
        if (CmbRulesGame.Items.Count > 0)
            CmbRulesGame.SelectedIndex = 0;

        SelectByTag(CmbCoachMode, _config.CoachMode);
        SelectByContent(CmbLanguage, _config.VoiceLanguage);
        SelectByContent(CmbEfficiency, _config.TokenEfficiencyMode);

        ChkEnableVoice.IsChecked = _config.EnableVoice;
        ChkEnableOverlay.IsChecked = _config.EnableOverlay;
        ChkEnableHotkeys.IsChecked = _config.EnableHotkeys;
        TxtPauseKey.Text = _config.HotkeyTogglePause;
        TxtAnalyzeKey.Text = _config.HotkeyAnalyzeNow;
        SldVolume.Value = _config.TtsVolume;

        _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity);
        RefreshAccountsList();
        AppendLog($"HUDMind Native (.NET 9) listo | Workspace: {_repo.WorkspaceRoot}");
        AppendLog($"Perfiles cargados: {_profiles.Count} | Cuentas IA listas: {_accounts.Count(a => a.IsReady())}");
    }

    private void RefreshAccountsList()
    {
        for (int i = 0; i < _accounts.Count; i++)
            _accounts[i].Priority = i + 1;
        LstAccounts.ItemsSource = _accounts.Select(a => new AccountRowView { Source = a }).ToList();
    }

    private void AppendLog(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtLiveLogs.AppendText(line);
            if (TxtLiveLogs.Text.Length > 28000)
                TxtLiveLogs.Text = TxtLiveLogs.Text[^18000..];
            TxtLiveLogs.ScrollToEnd();
        });
    }

    private void UpdateStatsUi(CoachRuntimeStats stats)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatCaptureMs.Text = stats.LastCaptureMs > 0 ? $"{stats.LastCaptureMs:F1} ms" : "— ms";
            StatAiMs.Text = stats.LastAiMs > 0 ? $"{stats.LastAiMs:F0} ms" : "— ms";
            StatVoiceMs.Text = stats.LastVoiceMs > 0 ? $"{stats.LastVoiceMs:F1} ms" : "— ms";
            StatInterventions.Text = $"{stats.Interventions} · {stats.ActiveGame}";
            HeaderStatusText.Text = $"Activo · {stats.ActiveProvider}";
        });
    }

    private async void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator is { IsRunning: true })
        {
            await ShutdownEngineAsync();
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

        _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity);

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
            onStats: UpdateStatsUi
        );

        BtnStartStop.IsEnabled = false;
        try
        {
            await _orchestrator.StartAsync();
            if (_config.EnableHotkeys)
            {
                _hotkeys.Start(
                    _config.HotkeyTogglePause,
                    _config.HotkeyAnalyzeNow,
                    () => _orchestrator?.TogglePause(),
                    () => _orchestrator?.TriggerOnDemandAnalysis()
                );
            }

            BtnStartStop.Content = "⏹ Detener Coach";
            BtnAnalyzeNow.IsEnabled = true;
            BtnPause.IsEnabled = true;
            HeaderStatusText.Text = $"Corriendo · {router.ActiveProviderLabel}";
        }
        catch (Exception ex)
        {
            AppendLog($"Error iniciando motor: {ex.Message}");
            await ShutdownEngineAsync();
        }
        finally
        {
            BtnStartStop.IsEnabled = true;
        }
    }

    private async Task ShutdownEngineAsync()
    {
        _hotkeys.Stop();
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync();
            _orchestrator = null;
        }
        _overlay.Hide();
        Dispatcher.Invoke(() =>
        {
            BtnStartStop.Content = "▶ Iniciar Coach";
            BtnAnalyzeNow.IsEnabled = false;
            BtnPause.IsEnabled = false;
            HeaderStatusText.Text = "Estado: Detenido";
        });
    }

    private void BtnAnalyzeNow_Click(object sender, RoutedEventArgs e) =>
        _orchestrator?.TriggerOnDemandAnalysis();

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator is null) return;
        _orchestrator.TogglePause();
        BtnPause.Content = _orchestrator.IsPaused ? "▶ Reanudar (F9)" : "⏸ Pausar (F9)";
    }

    private void CmbNewProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtNewLabel is null || TxtNewModel is null) return;
        var prov = (CmbNewProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "groq";
        (TxtNewLabel.Text, TxtNewModel.Text) = prov switch
        {
            "groq" => ("Groq LPU Gratis (~0.4s)", "meta-llama/llama-4-scout-17b-16e-instruct"),
            "gemini" => ("Gemini 2.5 Flash (~0.8s)", "gemini-2.5-flash"),
            "openrouter" => ("OpenRouter :free", "google/gemma-3-27b-it:free"),
            "openai" => ("OpenAI GPT-4o-mini", "gpt-4o-mini"),
            _ => ("Pollinations Gratis Sin Login", "openai-fast")
        };
    }

    private void BtnAddAccount_Click(object sender, RoutedEventArgs e)
    {
        var prov = (CmbNewProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "groq";
        var acc = new ProviderAccount
        {
            Label = string.IsNullOrWhiteSpace(TxtNewLabel.Text) ? prov : TxtNewLabel.Text.Trim(),
            Provider = prov,
            Model = TxtNewModel.Text.Trim(),
            ApiKey = TxtNewApiKey.Text.Trim(),
            Priority = 1,
            Enabled = true
        };
        if (!acc.IsReady())
        {
            System.Windows.MessageBox.Show("Pegá tu API Key para este proveedor.", "HUDMind", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _accounts.Insert(0, acc);
        TxtNewApiKey.Clear();
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
        await using var adapter = ProviderFailoverRouter.CreateDefaultAdapter(row.Source);
        var (ok, msg) = await adapter.TestConnectionAsync();
        AppendLog($"Test [{row.Label}]: {(ok ? "OK" : "ERROR")} — {msg}");
        System.Windows.MessageBox.Show(msg, ok ? "Conexión Exitosa" : "Error de Conexión", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private void BtnOpenGroq_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://console.groq.com/keys") { UseShellExecute = true });

    private void BtnOpenGemini_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true });

    private void CmbRulesGame_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var id = CmbRulesGame.SelectedItem?.ToString();
        var prof = _profiles.FirstOrDefault(p => p.Manifest.GameId == id);
        TxtCustomRules.Text = prof?.CustomRules ?? string.Empty;
    }

    private void TxtCustomRules_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LblRulesCount is not null)
            LblRulesCount.Text = $"{TxtCustomRules.Text.Length} / 2000 caracteres";
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

    private void ApplyConfigFromUi()
    {
        _config.CoachMode = GetSelectedTag(CmbCoachMode);
        _config.TextLanguage = (CmbLanguage.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "es";
        _config.VoiceLanguage = _config.TextLanguage;
        _config.TokenEfficiencyMode = (CmbEfficiency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "eco";
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
        AppendLog("Preferencias guardadas en config/user_config.json.");
    }

    private void BtnPreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        ApplyConfigFromUi();
        _overlay.Configure(_config.OverlayPosition, _config.OverlayWidth, _config.OverlayOpacity);
        _overlay.ShowCallout("Rotá segundo palo, tenés 18 de boost.", UrgencyLevel.High, "Rocket League", 4.0);
        if (_config.EnableVoice)
        {
            using var previewVoice = new InstantSapiVoiceService();
            previewVoice.WarmUp(_config.VoiceLanguage, _config.TtsVolume);
            double ms = previewVoice.SpeakInstant("Rotá segundo palo, tenés dieciocho de boost.");
            AppendLog($"Preview Voz SAPI5 disparado en {ms:F1} ms.");
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
