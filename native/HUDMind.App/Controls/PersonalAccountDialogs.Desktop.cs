using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Windows;
using TextBox = System.Windows.Controls.TextBox;

namespace HUDMind.App.Controls;

public sealed record FirstRunWizardResult(bool Completed, UserProfileRecord? UpdatedProfile, bool AccountAdded);

public static partial class PersonalAccountDialogs
{
    public static void ShowCrashReportDialog(Window? owner, string crashReportText, string? logFilePath)
    {
        var win = CreateModalWindow(owner, "HUDMind · Recuperación ante error inesperado", width: 560, height: 420);
        var root = new DockPanel { Margin = new Thickness(18) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock
        {
            Text = "⚠ HUDMind encontró un error inesperado",
            FontSize = 15.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextDanger
        });
        header.Children.Add(new TextBlock
        {
            Text = "Tus claves API fueron redactadas automáticamente (nunca aparecen en el log). Podés copiar este diagnóstico para enviarlo o revisar qué pasó:",
            Foreground = TextMuted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnCopy = CreateButton("📋 Copiar diagnóstico", BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        btnCopy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(crashReportText ?? string.Empty);
                btnCopy.Content = "✅ Copiado al portapapeles";
            }
            catch { }
        };
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            var btnFolder = CreateButton("📂 Abrir logs", BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
            btnFolder.Click += (_, _) =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch { }
            };
            footer.Children.Add(btnFolder);
        }
        var btnClose = CreateButton("Cerrar", BtnNeutralBg, BorderSubtle, new Thickness(0));
        btnClose.Click += (_, _) => win.Close();
        footer.Children.Add(btnCopy);
        footer.Children.Add(btnClose);
        root.Children.Add(footer);

        var txtReport = new TextBox
        {
            Text = crashReportText ?? string.Empty,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = BgInput,
            Foreground = FreezeBrush("#E2E8F0"),
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(10)
        };
        root.Children.Add(txtReport);

        win.Content = root;
        win.ShowDialog();
    }

    public static void ShowSystemDiagnosticsDialog(
        Window? owner,
        Func<bool, Task<List<DiagnosticCheckItem>>> runProbeAsync,
        string appVersion,
        IEnumerable<string>? activeApiKeys = null)
    {
        var win = CreateModalWindow(owner, "HUDMind · Diagnóstico del Sistema (0% GPU)", width: 640, height: 500);
        var root = new DockPanel { Margin = new Thickness(18) };
        var latestChecks = new List<DiagnosticCheckItem>();
        var keySnapshot = activeApiKeys?.ToArray() ?? [];

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock
        {
            Text = "🩺 CHEQUEO INTEGRAL DE EQUIPO Y SEGURIDAD",
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextAccent
        });
        top.Children.Add(new TextBlock
        {
            Text = "Verifica en modo lectura: 0% GPU SoftwareOnly, bóveda AES-256-GCM, monitores GDI, Windows OCR, micrófono/parlantes WinMM y puertos loopback.",
            Foreground = TextMuted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        root.Children.Add(top);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnReprobeMic = CreateButton("🎤 Probar + Micrófono (0.5s)", BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        var btnCopy = CreateButton("📋 Copiar reporte", BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnClose = CreateButton("Cerrar", BtnNeutralBg, BorderSubtle, new Thickness(0));
        btnClose.Click += (_, _) => win.Close();
        footer.Children.Add(btnReprobeMic);
        footer.Children.Add(btnCopy);
        footer.Children.Add(btnClose);
        root.Children.Add(footer);

        var listHost = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = listHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(scroll);

        async Task ExecuteProbeAsync(bool probeMic)
        {
            btnReprobeMic.IsEnabled = false;
            listHost.Children.Clear();
            listHost.Children.Add(new TextBlock
            {
                Text = probeMic ? "⏳ Analizando sistema y probando captura de micrófono..." : "⏳ Analizando subsistemas...",
                Foreground = TextAccent,
                Margin = new Thickness(4, 12, 4, 12)
            });

            var results = await runProbeAsync(probeMic);
            latestChecks = results;
            listHost.Children.Clear();
            foreach (var item in results)
            {
                var card = CreateCardBorder();
                card.Margin = new Thickness(0, 0, 0, 7);
                card.Padding = new Thickness(10, 8, 10, 8);
                card.BorderBrush = item.Passed ? FreezeBrush("#114E3A") : FreezeBrush("#78350F");

                var row = new DockPanel();
                var badge = new TextBlock
                {
                    Text = item.Passed ? "✅ OK" : "⚠ REVISAR",
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Foreground = item.Passed ? FreezeBrush("#34D399") : FreezeBrush("#FBBF24"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 92
                };
                DockPanel.SetDock(badge, Dock.Right);
                badge.TextAlignment = TextAlignment.Right;
                row.Children.Add(badge);

                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    Foreground = TextPrimary,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12
                });
                info.Children.Add(new TextBlock
                {
                    Text = item.Detail,
                    Foreground = TextMuted,
                    FontSize = 11.2,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                row.Children.Add(info);
                card.Child = row;
                listHost.Children.Add(card);
            }
            btnReprobeMic.IsEnabled = true;
        }

        btnReprobeMic.Click += async (_, _) => await ExecuteProbeAsync(probeMic: true);
        btnCopy.Click += (_, _) =>
        {
            try
            {
                var text = DiagnosticsFormatter.FormatHealthReport(appVersion, latestChecks, keySnapshot);
                System.Windows.Clipboard.SetText(text);
                btnCopy.Content = "✅ Copiado";
            }
            catch { }
        };

        win.Content = root;
        win.Loaded += async (_, _) => await ExecuteProbeAsync(probeMic: false);
        win.ShowDialog();
    }

    public static FirstRunWizardResult ShowFirstRunWizardDialog(
        Window? owner,
        UserProfileStore userStore,
        UserProfileRecord activeProfile,
        IReadOnlyList<GameProfile> gameProfiles,
        Func<bool> hasReadyAccount,
        Func<Window, Task<(bool Ok, string Msg)>> runOpenRouterPkceAsync,
        Func<Window, (bool Ok, string Key, string Label)> runGuidedGeminiAsync,
        Action<string> saveQuickGroqKey,
        Action openGroqConsole,
        Func<Task<MicProbeResult>> runMicTestAsync,
        Action runOverlayPreview)
    {
        bool completed = false;
        bool accountAdded = false;
        UserProfileRecord latestProfile = activeProfile;
        int currentStep = 1;

        var win = CreateModalWindow(owner, "HUDMind · Asistente de Bienvenida (3 pasos)", width: 590, height: 470);
        var root = new DockPanel { Margin = new Thickness(20) };

        // Step Progress Header
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(headerStack, Dock.Top);
        var lblStepTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextAccent };
        var lblStepSub = new TextBlock { FontSize = 11.5, Foreground = TextMuted, Margin = new Thickness(0, 3, 0, 8), TextWrapping = TextWrapping.Wrap };
        var stepProgress = new System.Windows.Controls.ProgressBar
        {
            Minimum = 1,
            Maximum = 3,
            Value = 1,
            Height = 5,
            Background = BgCard,
            Foreground = TextAccent,
            BorderThickness = new Thickness(0)
        };
        headerStack.Children.Add(lblStepTitle);
        headerStack.Children.Add(lblStepSub);
        headerStack.Children.Add(stepProgress);
        root.Children.Add(headerStack);

        // Footer navigation
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnSkip = CreateButton("Omitir asistente", BtnNeutralBg, BorderSubtle, new Thickness(0));
        DockPanel.SetDock(btnSkip, Dock.Left);
        var navRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnBack = CreateButton("← Anterior", BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        var btnNext = CreateButton("Siguiente →", BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        navRight.Children.Add(btnBack);
        navRight.Children.Add(btnNext);
        footer.Children.Add(btnSkip);
        footer.Children.Add(navRight);
        root.Children.Add(footer);

        // Step 1 Content: Profile & Favorite Game
        var step1 = new StackPanel();
        var s1Card = CreateCardBorder();
        var s1Body = new StackPanel();
        s1Body.Children.Add(CreateFieldLabel("Tu Nombre / Tag en el Coach:"));
        var txtName = CreateTextBox(activeProfile.DisplayName);
        s1Body.Children.Add(txtName);

        var rowPickers = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        rowPickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowPickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        rowPickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pAvatar = new StackPanel();
        pAvatar.Children.Add(CreateFieldLabel("Insignia:"));
        var cmbAvatar = new ComboBox();
        foreach (var av in AvatarChoices)
            cmbAvatar.Items.Add(new ComboBoxItem { Content = av, Tag = av });
        SelectComboTag(cmbAvatar, activeProfile.AvatarBadge);
        pAvatar.Children.Add(cmbAvatar);
        Grid.SetColumn(pAvatar, 0);
        rowPickers.Children.Add(pAvatar);

        var pAccent = new StackPanel();
        pAccent.Children.Add(CreateFieldLabel("Color del HUD:"));
        var cmbAccent = new ComboBox();
        foreach (var (colorName, hex) in AccentChoices)
            cmbAccent.Items.Add(new ComboBoxItem { Content = colorName, Tag = hex });
        SelectComboTag(cmbAccent, activeProfile.AccentHex);
        pAccent.Children.Add(cmbAccent);
        Grid.SetColumn(pAccent, 2);
        rowPickers.Children.Add(pAccent);
        s1Body.Children.Add(rowPickers);

        s1Body.Children.Add(CreateFieldLabel("Juego principal (podés cambiarlo en cualquier momento):", 10));
        var cmbGame = new ComboBox();
        cmbGame.Items.Add(new ComboBoxItem { Content = "🌐 Automático (detectar el juego abierto)", Tag = "" });
        foreach (var gp in gameProfiles.Where(g => !string.Equals(g.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase)))
            cmbGame.Items.Add(new ComboBoxItem { Content = $"🎮 {gp.Manifest.DisplayName}", Tag = gp.Manifest.GameId });
        SelectComboTag(cmbGame, activeProfile.FavoriteGameId);
        s1Body.Children.Add(cmbGame);
        s1Card.Child = s1Body;
        step1.Children.Add(s1Card);
        step1.Children.Add(new TextBlock
        {
            Text = $"🆔 Tu ID único permanente: {activeProfile.UniqueUserId}",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = FreezeBrush("#67E8F9"),
            FontSize = 11.5,
            Margin = new Thickness(4, 10, 0, 0)
        });

        // Step 2 Content: Connect AI ($0)
        var step2 = new StackPanel { Visibility = Visibility.Collapsed };
        var lblAiStatus = new TextBlock
        {
            Text = hasReadyAccount() ? "✅ Ya tenés una cuenta de IA conectada y lista." : "Todavía no hay ninguna API Key conectada en este perfil.",
            Foreground = hasReadyAccount() ? FreezeBrush("#34D399") : FreezeBrush("#FCD34D"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        step2.Children.Add(lblAiStatus);

        var s2Card1 = CreateCardBorder();
        s2Card1.Margin = new Thickness(0, 0, 0, 8);
        var rowBtns = new WrapPanel();
        var btnOr = CreateButton("🔐 Conectar OpenRouter (1 clic)", BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 6));
        var btnGem = CreateButton("✨ Conectar Gemini Live (2 clics)", FreezeBrush("#1E3A5F"), FreezeBrush("#38BDF8"), new Thickness(0, 0, 8, 6));
        var btnGroqWeb = CreateButton("🌐 Obtener clave Groq ($0)", FreezeBrush("#059669"), FreezeBrush("#10B981"), new Thickness(0, 0, 0, 6));
        rowBtns.Children.Add(btnOr);
        rowBtns.Children.Add(btnGem);
        rowBtns.Children.Add(btnGroqWeb);
        s2Card1.Child = rowBtns;
        step2.Children.Add(s2Card1);

        var s2Card2 = CreateCardBorder();
        var s2GroqStack = new StackPanel();
        s2GroqStack.Children.Add(CreateFieldLabel("¿Copiaste una API Key de Groq (gsk_...)? Pegala directo acá:"));
        var groqRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var btnSaveGroq = CreateButton("+ Guardar Groq", FreezeBrush("#059669"), FreezeBrush("#10B981"), new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(btnSaveGroq, Dock.Right);
        var pwdGroq = CreatePasswordBox();
        groqRow.Children.Add(btnSaveGroq);
        groqRow.Children.Add(pwdGroq);
        s2GroqStack.Children.Add(groqRow);
        s2Card2.Child = s2GroqStack;
        step2.Children.Add(s2Card2);

        // Step 3 Content: Audio & Overlay quick check
        var step3 = new StackPanel { Visibility = Visibility.Collapsed };
        var s3Card = CreateCardBorder();
        var s3Body = new StackPanel();
        s3Body.Children.Add(new TextBlock
        {
            Text = "Probá que HUDMind escuche tu voz y dibuje el cartel HUD 0% GPU sobre pantalla antes de entrar a ranked:",
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        var btnTestMic = CreateButton("🎤 Probar Micrófono (1s)", BtnNeutralBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnTestHud = CreateButton("👁 Probar Overlay + Voz", BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        testRow.Children.Add(btnTestMic);
        testRow.Children.Add(btnTestHud);
        s3Body.Children.Add(testRow);

        var lblMicResult = new TextBlock
        {
            Text = "Tip: si no querés hablar por micrófono, podés activar 'Solo texto' o Push-to-Talk en Ajustes.",
            Foreground = TextMuted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        s3Body.Children.Add(lblMicResult);
        s3Card.Child = s3Body;
        step3.Children.Add(s3Card);

        var bodyHost = new Grid();
        bodyHost.Children.Add(step1);
        bodyHost.Children.Add(step2);
        bodyHost.Children.Add(step3);
        root.Children.Add(bodyHost);

        void SyncStepUi()
        {
            stepProgress.Value = currentStep;
            step1.Visibility = currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            step2.Visibility = currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            step3.Visibility = currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            btnBack.IsEnabled = currentStep > 1;
            btnNext.Content = currentStep == 3 ? "🚀 Empezar a usar HUDMind" : "Siguiente →";

            (lblStepTitle.Text, lblStepSub.Text) = currentStep switch
            {
                1 => ("Paso 1 de 3 · Tu Perfil y Juego", "HUDMind aísla tus ajustes, claves DPAPI y memoria táctica de partidas bajo tu propio perfil local."),
                2 => ("Paso 2 de 3 · Conectá tu Cuenta IA ($0 Gratis)", "Usá tu propia clave gratis (OpenRouter en 1 clic, Google Gemini Live o Groq LPU). Nunca compartís claves con nadie."),
                _ => ("Paso 3 de 3 · Prueba Rápida de Voz y Overlay", "Confirmá tu audio y mirá cómo se ve el cartel flotante sin consumir VRAM de tu placa de video.")
            };
        }

        btnOr.Click += async (_, _) =>
        {
            btnOr.IsEnabled = false;
            lblAiStatus.Text = "⏳ Esperando autorización en el navegador...";
            var (ok, msg) = await runOpenRouterPkceAsync(win);
            btnOr.IsEnabled = true;
            lblAiStatus.Text = ok ? $"✅ {msg}" : $"⚠ {msg}";
            lblAiStatus.Foreground = ok ? FreezeBrush("#34D399") : TextDanger;
            if (ok) accountAdded = true;
        };

        btnGem.Click += (_, _) =>
        {
            var (ok, _, _) = runGuidedGeminiAsync(win);
            if (ok)
            {
                accountAdded = true;
                lblAiStatus.Text = "✅ Google Gemini Live conectado correctamente.";
                lblAiStatus.Foreground = FreezeBrush("#34D399");
            }
        };

        btnGroqWeb.Click += (_, _) => openGroqConsole();

        btnSaveGroq.Click += (_, _) =>
        {
            var k = pwdGroq.Password.Trim();
            if (string.IsNullOrWhiteSpace(k))
                return;
            saveQuickGroqKey(k);
            pwdGroq.Clear();
            accountAdded = true;
            lblAiStatus.Text = "✅ Clave de Groq guardada de forma segura con DPAPI.";
            lblAiStatus.Foreground = FreezeBrush("#34D399");
        };

        btnTestMic.Click += async (_, _) =>
        {
            btnTestMic.IsEnabled = false;
            lblMicResult.Text = "🎙 Hablá normal durante 1 segundo...";
            lblMicResult.Foreground = TextAccent;
            try
            {
                var probe = await runMicTestAsync();
                lblMicResult.Text = probe.SummaryMessage;
                lblMicResult.Foreground = probe.Opened && probe.ChunksCaptured > 0
                    ? FreezeBrush("#34D399")
                    : FreezeBrush("#FBBF24");
            }
            catch (Exception ex)
            {
                lblMicResult.Text = $"⚠ Error al probar micrófono: {ex.Message}";
                lblMicResult.Foreground = TextDanger;
            }
            btnTestMic.IsEnabled = true;
        };

        btnTestHud.Click += (_, _) => runOverlayPreview();

        btnBack.Click += (_, _) =>
        {
            if (currentStep > 1)
            {
                currentStep--;
                SyncStepUi();
            }
        };

        btnSkip.Click += (_, _) =>
        {
            completed = true;
            win.Close();
        };

        btnNext.Click += (_, _) =>
        {
            if (currentStep == 1)
            {
                var nextName = string.IsNullOrWhiteSpace(txtName.Text) ? activeProfile.DisplayName : txtName.Text.Trim();
                userStore.UpdateProfileMetadata(
                    activeProfile.ProfileId,
                    displayName: nextName,
                    avatarBadge: GetComboTag(cmbAvatar, activeProfile.AvatarBadge),
                    accentHex: GetComboTag(cmbAccent, activeProfile.AccentHex),
                    favoriteGameId: GetComboTag(cmbGame, activeProfile.FavoriteGameId));
                latestProfile = userStore.LoadOrInitialize().Profiles.FirstOrDefault(p => p.ProfileId == activeProfile.ProfileId)
                    ?? activeProfile;
                currentStep = 2;
                SyncStepUi();
                return;
            }
            if (currentStep == 2)
            {
                currentStep = 3;
                SyncStepUi();
                return;
            }
            completed = true;
            win.Close();
        };

        SyncStepUi();
        win.Content = root;
        win.ShowDialog();
        return new FirstRunWizardResult(completed, latestProfile, accountAdded);
    }
}
