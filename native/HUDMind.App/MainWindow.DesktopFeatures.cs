using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HUDMind.App.Controls;
using HUDMind.App.Services;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;
using HUDMind.Core.Security;
using HUDMind.Core.Updates;
using HUDMind.Windows;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace HUDMind.App;

public partial class MainWindow
{
    public void BringToFrontFromPipe()
    {
        if (_shutdownComplete || _smokeMode)
            return;

        RestoreFromTray();
        bool prevTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = prevTopmost;
        ShowToast("👋 HUDMind traído al frente.");
    }

    private void WireDesktopFeatureHandlers()
    {
        _overlay.CustomPositionCommitted += OnOverlayCustomPositionCommitted;
        NavHistory.Checked += (_, _) => RefreshHistoryDashboardUi();

        HotkeyRecorderHelper.AttachStandardHotkey(TxtPauseKey, k => ShowToast($"⌨ Tecla Pausar: {k}"));
        HotkeyRecorderHelper.AttachStandardHotkey(TxtAnalyzeKey, k => ShowToast($"⌨ Tecla Analizar: {k}"));
        HotkeyRecorderHelper.AttachStandardHotkey(TxtMicKey, k => ShowToast($"⌨ Tecla Micrófono: {k}"));
        HotkeyRecorderHelper.AttachStandardHotkey(TxtQuickChatKey, k => ShowToast($"⌨ Tecla Chat Rápido: {k}"));
        HotkeyRecorderHelper.AttachPushToTalkRecorder(TxtPttKey, k => ShowToast($"🎙 Push-to-Talk asignado a: {k}"));
    }

    private void ShowToast(string message, bool isWarning = false)
    {
        if (_shutdownComplete || _smokeMode || ToastBanner is null || TxtToastMessage is null)
            return;

        TxtToastMessage.Text = message;
        ToastBanner.Background = CreateStaticFrozenBrush(isWarning ? "#3B1D0A" : "#0B2D3E");
        ToastBanner.BorderBrush = CreateStaticFrozenBrush(isWarning ? "#F59E0B" : "#22D3EE");
        ToastBanner.Visibility = Visibility.Visible;

        if (_toastTimer is null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                if (ToastBanner is not null)
                    ToastBanner.Visibility = Visibility.Collapsed;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void RestoreWindowGeometryFromConfig()
    {
        if (_smokeMode)
            return;

        if (_config.WindowWidth is double savedW && _config.WindowHeight is double savedH &&
            savedW >= MinWidth && savedH >= MinHeight &&
            savedW <= 4096 && savedH <= 2160)
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
            double vHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);

            double clampedW = Math.Clamp(savedW, MinWidth, vWidth);
            double clampedH = Math.Clamp(savedH, MinHeight, vHeight);
            Width = clampedW;
            Height = clampedH;

            if (_config.WindowLeft is double savedL && _config.WindowTop is double savedT)
            {
                Left = Math.Clamp(savedL, vLeft, vLeft + Math.Max(0, vWidth - clampedW));
                Top = Math.Clamp(savedT, vTop, vTop + Math.Max(0, vHeight - clampedH));
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        if (!_startMinimized && _config.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void PersistWindowGeometryToConfig()
    {
        if (_smokeMode)
            return;

        try
        {
            bool isMax = WindowState == WindowState.Maximized;
            var rect = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height)
                : RestoreBounds;

            _config.WindowMaximized = isMax;
            if (!rect.IsEmpty && rect.Width >= MinWidth && rect.Height >= MinHeight)
            {
                _config.WindowLeft = Math.Round(rect.Left);
                _config.WindowTop = Math.Round(rect.Top);
                _config.WindowWidth = Math.Round(rect.Width);
                _config.WindowHeight = Math.Round(rect.Height);
            }
            _repo.SaveUserConfig(_config);
        }
        catch
        {
            // Keep exit path resilient even if workspace is read-only
        }
    }

    private void OnOverlayCustomPositionCommitted(double left, double top)
    {
        _config.OverlayCustomLeft = Math.Round(left);
        _config.OverlayCustomTop = Math.Round(top);
        _config.OverlayPosition = "custom";
        _syncingUi = true;
        try { SelectByTag(CmbOverlayPos, "custom"); }
        finally { _syncingUi = false; }
        _repo.SaveUserConfig(_config);
        ShowToast($"📌 Posición libre guardada ({_config.OverlayCustomLeft:F0}, {_config.OverlayCustomTop:F0}).");
    }

    private void BtnToggleOverlayDrag_Click(object sender, RoutedEventArgs e)
    {
        _overlayDragActive = !_overlayDragActive;
        if (_overlayDragActive && ChkEnableOverlay.IsChecked != true)
        {
            ChkEnableOverlay.IsChecked = true;
            _config.EnableOverlay = true;
        }

        ConfigureOverlayFromCurrentConfig();
        _overlay.SetDragPositionMode(_overlayDragActive);

        if (BtnToggleOverlayDrag is not null)
        {
            BtnToggleOverlayDrag.Content = AppLocalizationService.Get(
                _overlayDragActive ? "StrBtnLockOverlay" : "StrBtnMoveOverlay",
                _overlayDragActive ? "🔒 Fijar posición" : "📌 Mover Overlay");
            BtnToggleOverlayDrag.BorderBrush = CreateStaticFrozenBrush(_overlayDragActive ? "#10B981" : "#1E3348");
        }

        ShowToast(_overlayDragActive
            ? "📌 Arrastrá el cartel al lugar exacto que quieras y volvé a tocar '🔒 Fijar posición'."
            : "🔒 Posición del overlay bloqueada y transparente al mouse.");
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbLanguage is null)
            return;

        var lang = GetSelectedTag(CmbLanguage, "es");
        _config.TextLanguage = lang;
        _config.VoiceLanguage = lang;
        AppLocalizationService.ApplyLanguage(lang);
        if (BtnToggleOverlayDrag is not null)
        {
            BtnToggleOverlayDrag.Content = AppLocalizationService.Get(
                _overlayDragActive ? "StrBtnLockOverlay" : "StrBtnMoveOverlay",
                _overlayDragActive ? "🔒 Fijar posición" : "📌 Mover Overlay");
        }
        RefreshHotkeyButtonLabels();
        ShowToast(lang == "en" ? "🇺🇸 Interface language switched to English." : "🇪🇸 Idioma de interfaz cambiado a Español.");
    }

    private void BtnDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var (ver, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
        var verStr = GitHubReleaseUpdateService.FormatVersion(ver);
        PersonalAccountDialogs.ShowSystemDiagnosticsDialog(
            this,
            probeMic => SystemDiagnosticsProbe.RunChecksAsync(
                _config,
                _repo,
                "SoftwareOnly (0% GPU)",
                isCompanionActive: !string.IsNullOrEmpty(_companionHud.LocalUrl),
                isGsiActive: _coordinator.IsRunning && (_coordinator.ActiveEngine?.ActiveGameId is "cs2" or "dota2"),
                probeMicSample: probeMic && !_coordinator.IsRunning),
            verStr,
            GetAccountApiKeysSnapshot());
    }

    private void BtnRunWizard_Click(object sender, RoutedEventArgs e) =>
        RunFirstRunWizardInteractive();

    private void MaybeLaunchFirstRunWizardOnStartup()
    {
        if (_smokeMode || _startMinimized || _closingInProgress || _shutdownComplete)
            return;

        if (_config.HasCompletedOnboarding)
            return;

        bool hasReadyKey = HasAnyReadyUserApiKey();
        if (hasReadyKey)
        {
            // Silently skip wizard for existing users upgrading from prior releases who already configured keys.
            _config.HasCompletedOnboarding = true;
            _repo.SaveUserConfig(_config);
            return;
        }

        RunFirstRunWizardInteractive();
    }

    private bool HasAnyReadyUserApiKey()
    {
        lock (_accountsLock)
            return _accounts.Any(a => a.Enabled && !a.IsKeylessAllowed && !string.IsNullOrWhiteSpace(a.ApiKey));
    }

    private void RunFirstRunWizardInteractive()
    {
        if (_coordinator.IsRunning)
        {
            ShowToast("Detené primero el Coach Live para ejecutar el asistente inicial.", isWarning: true);
            return;
        }

        var result = PersonalAccountDialogs.ShowFirstRunWizardDialog(
            this,
            _userStore,
            _activeUserProfile,
            GetProfilesSnapshot(),
            hasReadyAccount: HasAnyReadyUserApiKey,
            runOpenRouterPkceAsync: async _ =>
            {
                var (ok, apiKey, errorMsg) = await OpenRouterOAuthPkceClient.AuthenticateAsync(
                    uri => OpenExternalUrl(uri.AbsoluteUri),
                    timeout: TimeSpan.FromMinutes(3));
                if (!ok || string.IsNullOrWhiteSpace(apiKey))
                    return (false, SecretRedactor.Redact(errorMsg, [apiKey]));

                var defaultModel = ProviderRegistry.FindPreset("openrouter")?.DefaultModel ?? "google/gemma-3-27b-it:free";
                UpsertQuickAccount("openrouter", $"OpenRouter · {_activeUserProfile.DisplayName}", defaultModel, apiKey, "OpenRouter 1-clic");
                return (true, $"OpenRouter conectado ({defaultModel}).");
            },
            runGuidedGeminiAsync: ownerWin =>
            {
                var (ok, key, label) = PersonalAccountDialogs.ShowGuidedGeminiKeyDialog(
                    ownerWin,
                    () => OpenExternalUrl("https://aistudio.google.com/apikey"));
                if (ok && !string.IsNullOrWhiteSpace(key))
                {
                    var model = ProviderRegistry.FindPreset("gemini")?.DefaultModel ?? "gemini-2.5-flash";
                    UpsertQuickAccount("gemini", label, model, key, "Gemini Live");
                    return (true, key, label);
                }
                return (false, string.Empty, string.Empty);
            },
            saveQuickGroqKey: key =>
            {
                var model = ProviderRegistry.FindPreset("groq")?.DefaultModel ?? "meta-llama/llama-4-scout-17b-16e-instruct";
                UpsertQuickAccount("groq", $"Groq · {_activeUserProfile.DisplayName}", model, key, "Groq LPU");
            },
            openGroqConsole: () => OpenExternalUrl("https://console.groq.com/keys"),
            runMicTestAsync: () => AudioDiagnosticsService.ProbeMicrophoneAsync(
                GetSelectedTag(CmbMicDevice, _config.AudioInputDeviceName),
                durationMs: 900),
            runOverlayPreview: () => BtnPreviewOverlay_Click(this, new RoutedEventArgs()));

        if (result.UpdatedProfile is not null)
        {
            _activeUserProfile = result.UpdatedProfile;
            RefreshPersonalProfileCardUi();
            RefreshPersonalWelcomeFeedItem();
            RefreshProfilePickers(selectMainId: _activeUserProfile.FavoriteGameId, selectRulesId: _activeUserProfile.FavoriteGameId);
        }

        if (result.Completed)
        {
            _config.HasCompletedOnboarding = true;
            _repo.SaveUserConfig(_config);
            RefreshOnboardingBanner();
            ShowToast("✅ Configuración inicial completada. ¡Listo para tu partida!");
        }
    }

    private void BtnFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning)
        {
            ShowToast("Detené primero el Coach Live para restaurar ajustes.", isWarning: true);
            return;
        }

        var ans = System.Windows.MessageBox.Show(
            "¿Restaurar voz, cartel overlay, idioma y teclas rápidas a valores de fábrica?\n\nTus API Keys, cuentas locales, reglas y memoria de partidas NO se borran.",
            "HUDMind · Ajustes de fábrica",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ans != MessageBoxResult.Yes)
            return;

        if (_overlayDragActive)
        {
            _overlayDragActive = false;
            _overlay.SetDragPositionMode(false);
            if (BtnToggleOverlayDrag is not null)
                BtnToggleOverlayDrag.Content = AppLocalizationService.Get("StrBtnMoveOverlay", "📌 Mover Overlay");
        }

        _config.ResetPreferencesToDefaults();
        _repo.SaveUserConfig(_config);
        AppLocalizationService.ApplyLanguage(_config.TextLanguage);
        _sharedVoice.SetOutputDevice(_config.AudioOutputDeviceName);
        _sharedVoice.SetSpeechRate(_config.TtsRate);
        _sharedVoice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
        BindInitialUi();
        _coordinator.ApplyLiveSettings(_config);
        _coordinator.RegisterHotkeys(_config, ToggleMicViaHotkey, AppendLog, ToggleQuickChatViaHotkey);
        AppendLog("↺ Ajustes de preferencia restaurados a valores de fábrica (cuentas y memoria conservadas).");
        ShowToast("↺ Ajustes restaurados a valores de fábrica.");
    }

    private void RefreshHistoryDashboardUi()
    {
        if (LstHistoryReviews is null || HistoryLeaksBarsHost is null || TxtHistoryRollingDetails is null)
            return;

        var selectedFile = (LstHistoryReviews.SelectedItem as ConfigRepository.PostMatchReviewSummary)?.FileName;
        var reviews = _repo.ListPostMatchReviews(60);
        LstHistoryReviews.ItemsSource = reviews;

        if (reviews.Count > 0)
        {
            var match = selectedFile is not null
                ? reviews.FirstOrDefault(r => string.Equals(r.FileName, selectedFile, StringComparison.OrdinalIgnoreCase))
                : null;
            LstHistoryReviews.SelectedItem = match ?? reviews[0];
        }
        else
        {
            TxtHistoryReviewViewer.Text = "Aún no guardaste resúmenes post-partida.\nAl terminar una sesión en vivo o modo prueba, tocá '📊 Resumen Partida' en el Observador.";
            LblHistoryReaderTitle.Text = "📖 Lector de Resumen";
            BtnDeleteReview.IsEnabled = false;
        }

        string gameFilter = CmbHistoryGameFilter?.SelectedValue?.ToString()
            ?? CmbGameProfile?.SelectedValue?.ToString()
            ?? "universal";
        if (string.IsNullOrWhiteSpace(gameFilter))
            gameFilter = "universal";

        RenderHistoryLeaksBars(gameFilter);
        RenderHistoryRollingTrend(gameFilter);
    }

    private void LstHistoryReviews_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtHistoryReviewViewer is null || LblHistoryReaderTitle is null || BtnDeleteReview is null)
            return;

        if (LstHistoryReviews.SelectedItem is not ConfigRepository.PostMatchReviewSummary item)
        {
            BtnDeleteReview.IsEnabled = false;
            return;
        }

        var content = _repo.ReadPostMatchReview(item.FileName);
        LblHistoryReaderTitle.Text = $"📖 {item.Title}";
        TxtHistoryReviewViewer.Text = string.IsNullOrWhiteSpace(content)
            ? "(Archivo vacío o no disponible)"
            : content;
        BtnDeleteReview.IsEnabled = true;
    }

    private void BtnRefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryDashboardUi();
        ShowToast("🔄 Historial y progreso actualizados.");
    }

    private void CmbHistoryGameFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi)
            return;
        var gameId = CmbHistoryGameFilter?.SelectedValue?.ToString() ?? "universal";
        RenderHistoryLeaksBars(gameId);
        RenderHistoryRollingTrend(gameId);
    }

    private void BtnDeleteReview_Click(object sender, RoutedEventArgs e)
    {
        if (LstHistoryReviews.SelectedItem is not ConfigRepository.PostMatchReviewSummary item)
            return;

        var ans = System.Windows.MessageBox.Show(
            $"¿Eliminar el resumen '{item.Title}' ({item.TimestampLabel})?",
            "HUDMind · Eliminar resumen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ans != MessageBoxResult.Yes)
            return;

        if (_repo.DeletePostMatchReview(item.FileName))
        {
            RefreshHistoryDashboardUi();
            ShowToast("🗑 Resumen eliminado.");
        }
    }

    private void RenderHistoryLeaksBars(string gameId)
    {
        HistoryLeaksBarsHost.Children.Clear();
        bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);

        PlayerLeakReport report;
        lock (_leakIoLock)
            report = _repo.LoadPlayerLeaks();

        string normGame = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        string todayIso = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");

        GameLeakCounters? counters = null;
        if (normGame == "universal")
        {
            var agg = new GameLeakCounters { CurrentDate = todayIso };
            foreach (var g in report.Games.Values)
            {
                PlayerLeakTracker.RollDateIfNeeded(g, todayIso);
                foreach (var kv in g.Today) agg.Today[kv.Key] = agg.Today.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var kv in g.Previous) agg.Previous[kv.Key] = agg.Previous.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var kv in g.Lifetime) agg.Lifetime[kv.Key] = agg.Lifetime.GetValueOrDefault(kv.Key) + kv.Value;
            }
            if (agg.Today.Count > 0 || agg.Previous.Count > 0 || agg.Lifetime.Count > 0)
                counters = agg;
        }
        else if (report.Games.TryGetValue(normGame, out var matched))
        {
            PlayerLeakTracker.RollDateIfNeeded(matched, todayIso);
            counters = matched;
        }

        if (counters is null || (counters.Today.Count == 0 && counters.Previous.Count == 0 && counters.Lifetime.Count == 0))
        {
            HistoryLeaksBarsHost.Children.Add(new TextBlock
            {
                Text = "Sin fugas tácticas registradas para este filtro todavía. HUDMind las detectará automáticamente cuando juegues con telemetría o visión.",
                Foreground = CreateStaticFrozenBrush("#8EA4BC"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var keys = counters.Today.Keys
            .Concat(counters.Previous.Keys)
            .Concat(counters.Lifetime.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(k => counters.Today.GetValueOrDefault(k) + counters.Previous.GetValueOrDefault(k) + counters.Lifetime.GetValueOrDefault(k))
            .Take(6)
            .ToList();

        int maxVal = Math.Max(1, keys.Max(k => Math.Max(counters.Today.GetValueOrDefault(k), counters.Previous.GetValueOrDefault(k))));

        foreach (var k in keys)
        {
            int cur = counters.Today.GetValueOrDefault(k);
            int prev = counters.Previous.GetValueOrDefault(k);
            int total = counters.Lifetime.GetValueOrDefault(k);
            string label = PlayerLeakTracker.GetLabel(k, isEn);

            var rowPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            string deltaBadge = prev > 0
                ? (cur < prev ? $"⬇ -{prev - cur} mejor" : cur == prev ? "➡ igual" : $"⬆ +{cur - prev}")
                : "nuevo";
            string deltaHex = prev > 0 && cur < prev ? "#34D399" : prev > 0 && cur > prev ? "#FBBF24" : "#94A3B8";

            var rightStat = new TextBlock
            {
                Text = $"Hoy: {cur} · Ant: {prev} · Hist: {total} ({deltaBadge})",
                Foreground = CreateStaticFrozenBrush(deltaHex),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10.5
            };
            DockPanel.SetDock(rightStat, Dock.Right);
            head.Children.Add(rightStat);
            head.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = CreateStaticFrozenBrush("#E2E8F0"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            rowPanel.Children.Add(head);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = maxVal,
                Value = cur,
                Height = 6,
                Background = CreateStaticFrozenBrush("#07101B"),
                Foreground = CreateStaticFrozenBrush(prev > 0 && cur <= prev ? "#10B981" : "#F59E0B"),
                BorderThickness = new Thickness(0)
            };
            rowPanel.Children.Add(bar);
            HistoryLeaksBarsHost.Children.Add(rowPanel);
        }
    }

    private void RenderHistoryRollingTrend(string gameId)
    {
        string normGame = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        PlayerGameMemory mem;
        lock (_memoryIoLock)
            mem = _repo.LoadPlayerGameMemory(normGame);

        if (mem.Matches.Count == 0)
        {
            TxtHistoryRollingDetails.Text = $"No hay memoria acumulada para '{normGame}' aún. Cada resumen post-partida alimenta automáticamente las últimas {PlayerMemoryStore.ClampWindowSize(_config.MemoryMatchWindow)} partidas.";
            return;
        }

        var lines = new List<string>
        {
            PlayerMemoryStore.FormatForUiSummary(mem),
            string.Empty,
            "Últimas partidas registradas:"
        };
        foreach (var m in mem.Matches.TakeLast(8).Reverse())
        {
            var focus = string.IsNullOrWhiteSpace(m.NextDrill) ? m.GameDisplayName : m.NextDrill;
            var habits = m.Habits.Count > 0 ? $" · Patrones: {string.Join(", ", m.Habits)}" : string.Empty;
            lines.Add($"• [{m.RecordedAtUtc.ToLocalTime():dd/MM HH:mm}] {focus}{habits}");
        }
        TxtHistoryRollingDetails.Text = string.Join(Environment.NewLine, lines);
    }

    private static SolidColorBrush CreateStaticFrozenBrush(string hex)
    {
        var b = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
