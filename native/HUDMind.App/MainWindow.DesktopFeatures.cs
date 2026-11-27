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
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace HUDMind.App;

public partial class MainWindow
{
    public void HandlePipeCommand(string? command)
    {
        if (_shutdownComplete || _smokeMode || string.IsNullOrWhiteSpace(command))
            return;

        string trimmed = command.Trim();
        if (trimmed.StartsWith("IMPORT:", StringComparison.OrdinalIgnoreCase))
        {
            BringToFrontFromPipe();
            string archivePath = trimmed["IMPORT:".Length..].Trim();
            ImportPersonalArchiveFromPath(archivePath);
            return;
        }

        if (string.Equals(trimmed, "SHOW", StringComparison.OrdinalIgnoreCase))
            BringToFrontFromPipe();
    }

    public void BringToFrontFromPipe()
    {
        if (_shutdownComplete || _smokeMode)
            return;

        RestoreFromTray();
        bool prevTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = prevTopmost;
        ShowToast(AppLocalizationService.T("👋 HUDMind traído al frente.", "👋 HUDMind brought to front."));
    }

    private void WireDesktopFeatureHandlers()
    {
        _overlay.CustomPositionCommitted += OnOverlayCustomPositionCommitted;
        NavHistory.Checked += (_, _) => RefreshHistoryDashboardUi();

        HotkeyRecorderHelper.AttachStandardHotkey(TxtPauseKey, k => ShowToast(AppLocalizationService.T($"⌨ Tecla Pausar: {k}", $"⌨ Pause Key: {k}")));
        HotkeyRecorderHelper.AttachStandardHotkey(TxtAnalyzeKey, k => ShowToast(AppLocalizationService.T($"⌨ Tecla Analizar: {k}", $"⌨ Analyze Key: {k}")));
        HotkeyRecorderHelper.AttachStandardHotkey(TxtMicKey, k => ShowToast(AppLocalizationService.T($"⌨ Tecla Micrófono: {k}", $"⌨ Mic Mute Key: {k}")));
        HotkeyRecorderHelper.AttachStandardHotkey(TxtQuickChatKey, k => ShowToast(AppLocalizationService.T($"⌨ Tecla Chat Rápido: {k}", $"⌨ Quick Chat Key: {k}")));
        HotkeyRecorderHelper.AttachPushToTalkRecorder(TxtPttKey, k => ShowToast(AppLocalizationService.T($"🎙 Push-to-Talk asignado a: {k}", $"🎙 Push-to-Talk bound to: {k}")));
    }

    private void MainWindow_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (TryGetSingleDraggedArchivePath(e.Data, out _))
            e.Effects = System.Windows.DragDropEffects.Copy;
        else
            e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void MainWindow_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (TryGetSingleDraggedArchivePath(e.Data, out var path) && path is not null)
        {
            e.Handled = true;
            ImportPersonalArchiveFromPath(path);
        }
    }

    private static bool TryGetSingleDraggedArchivePath(System.Windows.IDataObject? data, out string? path)
    {
        path = null;
        if (data is null || !data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            return false;
        if (data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length != 1)
            return false;
        if (!AppReleaseHighlights.IsValidBackupArchivePath(files[0]))
            return false;
        path = files[0];
        return true;
    }

    private void ImportPersonalArchiveFromPath(string rawFilePath)
    {
        if (_coordinator.IsRunning)
        {
            ShowToast(AppLocalizationService.T("Detené primero el Coach Live antes de restaurar un backup.", "Stop Live Coach first before restoring a backup."), isWarning: true);
            return;
        }

        if (!AppReleaseHighlights.IsValidBackupArchivePath(rawFilePath))
        {
            ShowToast(AppLocalizationService.T("Archivo inválido: elegí un backup .hudmind local.", "Invalid archive: select a local .hudmind backup file."), isWarning: true);
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawFilePath.Trim().Trim('"'));
            if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                ShowToast(AppLocalizationService.T("No se pudo abrir el archivo .hudmind (no encontrado o enlace bloqueado).", "Unable to open .hudmind file (missing or symlink blocked)."), isWarning: true);
                return;
            }
            var info = new FileInfo(fullPath);
            if (info.Length <= 0 || info.Length > AppReleaseHighlights.MaxArchiveBytes)
            {
                ShowToast(AppLocalizationService.T("Tamaño de backup .hudmind inválido (máx. 10 MB).", "Invalid .hudmind backup size (max 10 MB)."), isWarning: true);
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"⚠ Error validando .hudmind: {SecretRedactor.Redact(ex.Message)}");
            return;
        }

        var (ok, passphrase, importKeys) = PersonalAccountDialogs.ShowPassphraseDialog(this, isExport: false);
        if (!ok)
            return;

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            var importRes = ProfileArchiveCodec.ImportBundleDetailed(bytes, _repo, passphrase, importApiAccounts: importKeys);
            if (importRes.Metadata is { } meta)
            {
                _userStore.UpdateProfileMetadata(
                    _activeUserProfile.ProfileId,
                    meta.DisplayName,
                    meta.AvatarBadge,
                    meta.AccentHex,
                    meta.FavoriteGameId);
                var refreshed = _userStore.LoadOrInitialize().Profiles
                    .FirstOrDefault(p => p.ProfileId == _activeUserProfile.ProfileId);
                if (refreshed is not null)
                    _activeUserProfile = refreshed;
            }

            ReloadWorkspaceForActiveProfile(_activeUserProfile);
            string keysNote = importRes.ImportedApiAccounts
                ? AppLocalizationService.T("Incluyó cuentas IA cifradas.", "Included encrypted AI accounts.")
                : AppLocalizationService.T("No incluyó claves IA (revisá Proveedores IA si necesitás conectar una).", "AI keys not included (check AI Providers if needed).");
            AppendLog($"📥 Backup restaurado ({importRes.FilesWritten} archivos) en perfil '{_activeUserProfile.DisplayName}'. {keysNote}");
            ShowToast(AppLocalizationService.T(
                $"📥 Backup restaurado en '{_activeUserProfile.DisplayName}' ({importRes.FilesWritten} archivos).",
                $"📥 Restored backup into '{_activeUserProfile.DisplayName}' ({importRes.FilesWritten} files)."));
        }
        catch (Exception ex)
        {
            var safe = SecretRedactor.Redact(ex.Message);
            AppendLog($"⚠ Error importando .hudmind: {safe}");
            System.Windows.MessageBox.Show(
                AppLocalizationService.T("Contraseña incorrecta o archivo .hudmind inválido/dañado.", "Wrong passphrase or invalid/corrupted .hudmind file."),
                AppLocalizationService.T("Error de Importación", "Import Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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
            // Keep exit path resilient
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
        ShowToast(AppLocalizationService.T(
            $"📌 Posición libre guardada ({_config.OverlayCustomLeft:F0}, {_config.OverlayCustomTop:F0}).",
            $"📌 Custom position saved ({_config.OverlayCustomLeft:F0}, {_config.OverlayCustomTop:F0})."));
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
            ? AppLocalizationService.T("📌 Arrastrá el cartel al lugar exacto que quieras y volvé a tocar '🔒 Fijar posición'.", "📌 Drag the overlay anywhere you want, then click '🔒 Lock Position'.")
            : AppLocalizationService.T("🔒 Posición del overlay bloqueada y transparente al mouse.", "🔒 Overlay position locked and click-through restored."));
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || CmbLanguage is null)
            return;

        var lang = GetSelectedTag(CmbLanguage, "es");
        _config.TextLanguage = lang;
        _config.VoiceLanguage = lang;
        AppLocalizationService.ApplyLanguage(lang);
        RefreshLocalizedUiElements();
        ShowToast(lang == "en" ? "🇺🇸 Interface language switched to English." : "🇪🇸 Idioma de interfaz cambiado a Español.");
    }

    private void RefreshLocalizedUiElements()
    {
        string? curGame = CmbGameProfile?.SelectedValue?.ToString();
        string? curRulesGame = CmbRulesGame?.SelectedValue?.ToString();
        RefreshProfilePickers(selectMainId: curGame, selectRulesId: curRulesGame);

        _syncingUi = true;
        try
        {
            var curMic = GetSelectedTag(CmbMicDevice, _config.AudioInputDeviceName);
            var curSpk = GetSelectedTag(CmbSpeakerDevice, _config.AudioOutputDeviceName);
            RefreshMonitorsList();
            RefreshAudioDevicesList();
            SelectByTag(CmbMicDevice, curMic);
            SelectByTag(CmbSpeakerDevice, curSpk);
        }
        finally
        {
            _syncingUi = false;
        }

        if (BtnToggleOverlayDrag is not null)
        {
            BtnToggleOverlayDrag.Content = AppLocalizationService.Get(
                _overlayDragActive ? "StrBtnLockOverlay" : "StrBtnMoveOverlay",
                _overlayDragActive ? "🔒 Fijar posición" : "📌 Mover Overlay");
        }

        if (!_coordinator.IsRunning && HeaderStatusText is not null)
            HeaderStatusText.Text = AppLocalizationService.T("Listo para jugar", "Ready to play");

        RefreshPersonalProfileCardUi();
        RefreshPersonalWelcomeFeedItem();
        RefreshAccountsList();
        RefreshHotkeyButtonLabels();
        ResetStatsCards();
        RefreshRollingMemoryUi();
        RefreshHistoryDashboardUi();
        UpdateOverlaySliderLabels();
        ConfigureOverlayFromCurrentConfig();
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

    private void BtnWhatsNew_Click(object sender, RoutedEventArgs e)
    {
        var (ver, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
        var (title, body) = AppReleaseHighlights.GetReleaseNotes(ver, AppLocalizationService.IsEnglish);
        PersonalAccountDialogs.ShowWhatsNewDialog(this, title, body);
    }

    private void MaybeLaunchFirstRunWizardOnStartup()
    {
        if (_smokeMode || _startMinimized || _closingInProgress || _shutdownComplete)
            return;

        if (!string.IsNullOrWhiteSpace(_startupArchivePath))
            ImportPersonalArchiveFromPath(_startupArchivePath);

        if (!_config.HasCompletedOnboarding)
        {
            if (HasAnyReadyUserApiKey())
            {
                _config.HasCompletedOnboarding = true;
                _repo.SaveUserConfig(_config);
            }
            else
            {
                RunFirstRunWizardInteractive();
            }
        }

        MaybeShowWhatsNewOnStartup();
    }

    private void MaybeShowWhatsNewOnStartup()
    {
        if (_smokeMode || _startMinimized || _closingInProgress || _shutdownComplete)
            return;

        var (currentVer, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
        var formattedCurr = GitHubReleaseUpdateService.FormatVersion(currentVer);
        bool upgraded = AppReleaseHighlights.ShouldShowWhatsNew(_config.LastSeenAppVersion, currentVer);

        if (!string.Equals(_config.LastSeenAppVersion, formattedCurr, StringComparison.OrdinalIgnoreCase))
        {
            _config.LastSeenAppVersion = formattedCurr;
            _repo.SaveUserConfig(_config);
        }

        if (upgraded && _config.HasCompletedOnboarding)
        {
            var (title, body) = AppReleaseHighlights.GetReleaseNotes(currentVer, AppLocalizationService.IsEnglish);
            PersonalAccountDialogs.ShowWhatsNewDialog(this, title, body);
        }
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
            ShowToast(AppLocalizationService.T("Detené primero el Coach Live para ejecutar el asistente inicial.", "Stop Live Coach first before running the Setup Wizard."), isWarning: true);
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
                return (true, $"OpenRouter OK ({defaultModel}).");
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
            var (ver, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
            _config.HasCompletedOnboarding = true;
            _config.LastSeenAppVersion = GitHubReleaseUpdateService.FormatVersion(ver);
            _repo.SaveUserConfig(_config);
            RefreshOnboardingBanner();
            ShowToast(AppLocalizationService.T("✅ Configuración inicial completada. ¡Listo para tu partida!", "✅ Quick setup completed. Ready for your match!"));
        }
    }

    private void BtnFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning)
        {
            ShowToast(AppLocalizationService.T("Detené primero el Coach Live para restaurar ajustes.", "Stop Live Coach before resetting settings."), isWarning: true);
            return;
        }

        var ans = System.Windows.MessageBox.Show(
            AppLocalizationService.T(
                "¿Restaurar voz, cartel overlay, idioma y teclas rápidas a valores de fábrica?\n\nTus API Keys, cuentas locales, reglas y memoria de partidas NO se borran.",
                "Restore voice, overlay, language, and hotkeys back to factory defaults?\n\nYour API keys, local profiles, game rules, and match memory are preserved."),
            AppLocalizationService.T("HUDMind · Ajustes de fábrica", "HUDMind · Factory Reset"),
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
        RefreshLocalizedUiElements();
        _coordinator.ApplyLiveSettings(_config);
        _coordinator.RegisterHotkeys(_config, ToggleMicViaHotkey, AppendLog, ToggleQuickChatViaHotkey);
        AppendLog("↺ Ajustes de preferencia restaurados a valores de fábrica (cuentas y memoria conservadas).");
        ShowToast(AppLocalizationService.T("↺ Ajustes restaurados a valores de fábrica.", "↺ Settings restored to factory defaults."));
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
            TxtHistoryReviewViewer.Text = AppLocalizationService.T(
                "Aún no guardaste resúmenes post-partida.\nAl terminar una sesión en vivo o modo prueba, tocá '📊 Resumen Partida' en el Observador.",
                "No post-match reviews saved yet.\nAfter finishing a live or test session, click '📊 Match Review' on the Observer tab.");
            LblHistoryReaderTitle.Text = AppLocalizationService.T("📖 Lector de Resumen", "📖 Review Reader");
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
            ? AppLocalizationService.T("(Archivo vacío o no disponible)", "(Empty or unavailable review)")
            : content;
        BtnDeleteReview.IsEnabled = true;
    }

    private void BtnRefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryDashboardUi();
        ShowToast(AppLocalizationService.T("🔄 Historial y progreso actualizados.", "🔄 History and progress refreshed."));
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
            AppLocalizationService.T($"¿Eliminar el resumen '{item.Title}' ({item.TimestampLabel})?", $"Delete review '{item.Title}' ({item.TimestampLabel})?"),
            AppLocalizationService.T("HUDMind · Eliminar resumen", "HUDMind · Delete Review"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ans != MessageBoxResult.Yes)
            return;

        if (_repo.DeletePostMatchReview(item.FileName))
        {
            RefreshHistoryDashboardUi();
            ShowToast(AppLocalizationService.T("🗑 Resumen eliminado.", "🗑 Review deleted."));
        }
    }

    private void RenderHistoryLeaksBars(string gameId)
    {
        HistoryLeaksBarsHost.Children.Clear();
        bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true) || AppLocalizationService.IsEnglish;

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
                Text = AppLocalizationService.T(
                    "Sin fugas tácticas registradas para este filtro todavía. HUDMind las detectará automáticamente cuando juegues con telemetría o visión.",
                    "No repeated tactical leaks recorded for this filter yet. HUDMind detects them automatically while you play."),
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
                ? (cur < prev ? AppLocalizationService.T($"⬇ -{prev - cur} mejor", $"⬇ -{prev - cur} better") : cur == prev ? AppLocalizationService.T("➡ igual", "➡ flat") : $"⬆ +{cur - prev}")
                : AppLocalizationService.T("nuevo", "new");
            string deltaHex = prev > 0 && cur < prev ? "#34D399" : prev > 0 && cur > prev ? "#FBBF24" : "#94A3B8";

            var rightStat = new TextBlock
            {
                Text = AppLocalizationService.T($"Hoy: {cur} · Ant: {prev} · Hist: {total} ({deltaBadge})", $"Today: {cur} · Prev: {prev} · All: {total} ({deltaBadge})"),
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
            TxtHistoryRollingDetails.Text = AppLocalizationService.T(
                $"No hay memoria acumulada para '{normGame}' aún. Cada resumen post-partida alimenta automáticamente las últimas {PlayerMemoryStore.ClampWindowSize(_config.MemoryMatchWindow)} partidas.",
                $"No rolling match memory recorded for '{normGame}' yet. Each saved review feeds your last {PlayerMemoryStore.ClampWindowSize(_config.MemoryMatchWindow)} matches.");
            return;
        }

        var lines = new List<string>
        {
            PlayerMemoryStore.FormatForUiSummary(mem),
            string.Empty,
            AppLocalizationService.T("Últimas partidas registradas:", "Recent tracked matches:")
        };
        foreach (var m in mem.Matches.TakeLast(8).Reverse())
        {
            var focus = string.IsNullOrWhiteSpace(m.NextDrill) ? m.GameDisplayName : m.NextDrill;
            var habits = m.Habits.Count > 0 ? $" · {AppLocalizationService.T("Patrones", "Habits")}: {string.Join(", ", m.Habits)}" : string.Empty;
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
