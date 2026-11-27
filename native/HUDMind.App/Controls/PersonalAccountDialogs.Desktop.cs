using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HUDMind.App.Services;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Windows;
using TextBox = System.Windows.Controls.TextBox;

namespace HUDMind.App.Controls;

public sealed record FirstRunWizardResult(bool Completed, UserProfileRecord? UpdatedProfile, bool AccountAdded);

public static partial class PersonalAccountDialogs
{
    public static void ShowWhatsNewDialog(Window? owner, string title, string summaryBody)
    {
        var win = CreateModalWindow(owner, title, width: 550, height: 370);
        var root = new DockPanel { Margin = new Thickness(20) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = TextAccent
        });
        header.Children.Add(new TextBlock
        {
            Text = L("HUDMind se actualizó en tu equipo con 0% GPU y cero dependencias externas:", "HUDMind is ready with 0% GPU impact and zero external NuGet dependencies:"),
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
            Margin = new Thickness(0, 14, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnOk = CreateButton(L("✅ ¡Entendido!", "✅ Got it!"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        btnOk.Click += (_, _) => win.Close();
        footer.Children.Add(btnOk);
        root.Children.Add(footer);

        var card = CreateCardBorder();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = summaryBody,
                Foreground = TextPrimary,
                FontSize = 12.2,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap
            }
        };
        card.Child = scroll;
        root.Children.Add(card);

        win.Content = root;
        win.ShowDialog();
    }

    public static void ShowCrashReportDialog(Window? owner, string crashReportText, string? logFilePath)
    {
        var win = CreateModalWindow(owner, L("HUDMind · Recuperación ante error inesperado", "HUDMind · Unexpected Error Recovery"), width: 560, height: 420);
        var root = new DockPanel { Margin = new Thickness(18) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock
        {
            Text = L("⚠ HUDMind encontró un error inesperado", "⚠ HUDMind encountered an unexpected error"),
            FontSize = 15.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextDanger
        });
        header.Children.Add(new TextBlock
        {
            Text = L(
                "Tus claves API fueron redactadas automáticamente (nunca aparecen en el log). Podés copiar este diagnóstico para enviarlo o revisar qué pasó:",
                "All API keys and tokens were automatically redacted from this report. You can copy it below or inspect the log folder:"),
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
        var btnCopy = CreateButton(L("📋 Copiar diagnóstico", "📋 Copy diagnostics"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        btnCopy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(crashReportText ?? string.Empty);
                btnCopy.Content = L("✅ Copiado al portapapeles", "✅ Copied to clipboard");
            }
            catch { }
        };
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            var btnFolder = CreateButton(L("📂 Abrir logs", "📂 Open logs"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
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
        var btnClose = CreateButton(L("Cerrar", "Close"), BtnNeutralBg, BorderSubtle, new Thickness(0));
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
        var win = CreateModalWindow(owner, L("HUDMind · Diagnóstico del Sistema (0% GPU)", "HUDMind · System Health Diagnostics (0% GPU)"), width: 640, height: 500);
        var root = new DockPanel { Margin = new Thickness(18) };
        var latestChecks = new List<DiagnosticCheckItem>();
        var keySnapshot = activeApiKeys?.ToArray() ?? [];

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock
        {
            Text = L("🩺 CHEQUEO INTEGRAL DE EQUIPO Y SEGURIDAD", "🩺 SYSTEM HARDWARE & SECURITY HEALTH CHECK"),
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextAccent
        });
        top.Children.Add(new TextBlock
        {
            Text = L(
                "Verifica en modo lectura: 0% GPU SoftwareOnly, bóveda AES-256-GCM, monitores GDI, Windows OCR, micrófono/parlantes WinMM y puertos loopback.",
                "Read-only check verifying 0% GPU SoftwareOnly mode, AES-256-GCM vault integrity, GDI displays, WinRT OCR, WinMM mic/speakers, and loopback ports."),
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
        var btnReprobeMic = CreateButton(L("🎤 Probar + Micrófono (0.5s)", "🎤 Re-test + Microphone (0.5s)"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        var btnCopy = CreateButton(L("📋 Copiar reporte", "📋 Copy report"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnClose = CreateButton(L("Cerrar", "Close"), BtnNeutralBg, BorderSubtle, new Thickness(0));
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
                Text = probeMic
                    ? L("⏳ Analizando sistema y probando captura de micrófono...", "⏳ Running system diagnostics and microphone capture...")
                    : L("⏳ Analizando subsistemas...", "⏳ Checking subsystems..."),
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
                    Text = item.Passed ? "✅ OK" : L("⚠ REVISAR", "⚠ CHECK"),
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
                btnCopy.Content = L("✅ Copiado", "✅ Copied");
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

        var win = CreateModalWindow(owner, L("HUDMind · Asistente de Bienvenida (3 pasos)", "HUDMind · Quick Setup Wizard (3 Steps)"), width: 590, height: 470);
        var root = new DockPanel { Margin = new Thickness(20) };

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

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnSkip = CreateButton(L("Omitir asistente", "Skip wizard"), BtnNeutralBg, BorderSubtle, new Thickness(0));
        DockPanel.SetDock(btnSkip, Dock.Left);
        var navRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnBack = CreateButton(L("← Anterior", "← Back"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        var btnNext = CreateButton(L("Siguiente →", "Next →"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        navRight.Children.Add(btnBack);
        navRight.Children.Add(btnNext);
        footer.Children.Add(btnSkip);
        footer.Children.Add(navRight);
        root.Children.Add(footer);

        // Step 1 Content: Profile & Favorite Game
        var step1 = new StackPanel();
        var s1Card = CreateCardBorder();
        var s1Body = new StackPanel();
        s1Body.Children.Add(CreateFieldLabel(L("Tu Nombre / Tag en el Coach:", "Display Name / Gamertag:")));
        var txtName = CreateTextBox(activeProfile.DisplayName);
        s1Body.Children.Add(txtName);

        var rowPickers = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        rowPickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowPickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        rowPickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pAvatar = new StackPanel();
        pAvatar.Children.Add(CreateFieldLabel(L("Insignia:", "Badge:")));
        var cmbAvatar = new ComboBox();
        foreach (var av in AvatarChoices)
            cmbAvatar.Items.Add(new ComboBoxItem { Content = av, Tag = av });
        SelectComboTag(cmbAvatar, activeProfile.AvatarBadge);
        pAvatar.Children.Add(cmbAvatar);
        Grid.SetColumn(pAvatar, 0);
        rowPickers.Children.Add(pAvatar);

        var pAccent = new StackPanel();
        pAccent.Children.Add(CreateFieldLabel(L("Color del HUD:", "HUD Accent Color:")));
        var cmbAccent = new ComboBox();
        foreach (var (colorName, hex) in AccentChoices)
            cmbAccent.Items.Add(new ComboBoxItem { Content = colorName, Tag = hex });
        SelectComboTag(cmbAccent, activeProfile.AccentHex);
        pAccent.Children.Add(cmbAccent);
        Grid.SetColumn(pAccent, 2);
        rowPickers.Children.Add(pAccent);
        s1Body.Children.Add(rowPickers);

        s1Body.Children.Add(CreateFieldLabel(L("Juego principal (podés cambiarlo en cualquier momento):", "Primary game (changeable anytime):"), 10));
        var cmbGame = new ComboBox();
        cmbGame.Items.Add(new ComboBoxItem { Content = L("🌐 Automático (detectar el juego abierto)", "🌐 Automatic (detect active game)"), Tag = "" });
        foreach (var gp in gameProfiles.Where(g => !string.Equals(g.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase)))
            cmbGame.Items.Add(new ComboBoxItem { Content = $"🎮 {gp.Manifest.DisplayName}", Tag = gp.Manifest.GameId });
        SelectComboTag(cmbGame, activeProfile.FavoriteGameId);
        s1Body.Children.Add(cmbGame);
        s1Card.Child = s1Body;
        step1.Children.Add(s1Card);
        step1.Children.Add(new TextBlock
        {
            Text = L($"🆔 Tu ID único permanente: {activeProfile.UniqueUserId}", $"🆔 Your permanent Unique ID: {activeProfile.UniqueUserId}"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = FreezeBrush("#67E8F9"),
            FontSize = 11.5,
            Margin = new Thickness(4, 10, 0, 0)
        });

        // Step 2 Content: Connect AI ($0)
        var step2 = new StackPanel { Visibility = Visibility.Collapsed };
        var lblAiStatus = new TextBlock
        {
            Text = hasReadyAccount()
                ? L("✅ Ya tenés una cuenta de IA conectada y lista.", "✅ You already have a ready AI provider connected.")
                : L("Todavía no hay ninguna API Key conectada en este perfil.", "No API Key is connected to this profile yet."),
            Foreground = hasReadyAccount() ? FreezeBrush("#34D399") : FreezeBrush("#FCD34D"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        step2.Children.Add(lblAiStatus);

        var s2Card1 = CreateCardBorder();
        s2Card1.Margin = new Thickness(0, 0, 0, 8);
        var rowBtns = new WrapPanel();
        var btnOr = CreateButton(L("🔐 Conectar OpenRouter (1 clic)", "🔐 Sign in OpenRouter (1 click)"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 6));
        var btnGem = CreateButton(L("✨ Conectar Gemini Live (2 clics)", "✨ Connect Gemini Live (2 clicks)"), FreezeBrush("#1E3A5F"), FreezeBrush("#38BDF8"), new Thickness(0, 0, 8, 6));
        var btnGroqWeb = CreateButton(L("🌐 Obtener clave Groq ($0)", "🌐 Get Free Groq Key ($0)"), FreezeBrush("#059669"), FreezeBrush("#10B981"), new Thickness(0, 0, 0, 6));
        rowBtns.Children.Add(btnOr);
        rowBtns.Children.Add(btnGem);
        rowBtns.Children.Add(btnGroqWeb);
        s2Card1.Child = rowBtns;
        step2.Children.Add(s2Card1);

        var s2Card2 = CreateCardBorder();
        var s2GroqStack = new StackPanel();
        s2GroqStack.Children.Add(CreateFieldLabel(L("¿Copiaste una API Key de Groq (gsk_...)? Pegala directo acá:", "Copied a Groq API Key (gsk_...)? Paste it right here:")));
        var groqRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var btnSaveGroq = CreateButton(L("+ Guardar Groq", "+ Save Groq"), FreezeBrush("#059669"), FreezeBrush("#10B981"), new Thickness(8, 0, 0, 0));
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
            Text = L(
                "Probá que HUDMind escuche tu voz y dibuje el cartel HUD 0% GPU sobre pantalla antes de entrar a ranked:",
                "Verify microphone input and preview the 0% GPU floating overlay before hopping into a match:"),
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        var btnTestMic = CreateButton(L("🎤 Probar Micrófono (1s)", "🎤 Test Microphone (1s)"), BtnNeutralBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnTestHud = CreateButton(L("👁 Probar Overlay + Voz", "👁 Test Overlay + Voice"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        testRow.Children.Add(btnTestMic);
        testRow.Children.Add(btnTestHud);
        s3Body.Children.Add(testRow);

        var lblMicResult = new TextBlock
        {
            Text = L(
                "Tip: si no querés hablar por micrófono, podés activar 'Solo texto' o Push-to-Talk en Ajustes.",
                "Tip: if you prefer zero voice input, switch to 'Text Only' or Push-to-Talk in Settings."),
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
            btnNext.Content = currentStep == 3
                ? L("🚀 Empezar a usar HUDMind", "🚀 Start Using HUDMind")
                : L("Siguiente →", "Next →");

            (lblStepTitle.Text, lblStepSub.Text) = currentStep switch
            {
                1 => (L("Paso 1 de 3 · Tu Perfil y Juego", "Step 1 of 3 · Profile & Favorite Game"),
                      L("HUDMind aísla tus ajustes, claves DPAPI y memoria táctica de partidas bajo tu propio perfil local.", "HUDMind isolates your settings, DPAPI-encrypted keys, and match memory inside your local profile.")),
                2 => (L("Paso 2 de 3 · Conectá tu Cuenta IA ($0 Gratis)", "Step 2 of 3 · Connect Free AI ($0)"),
                      L("Usá tu propia clave gratis (OpenRouter en 1 clic, Google Gemini Live o Groq LPU). Nunca compartís claves con nadie.", "Use your own free key via 1-click OpenRouter PKCE, Google Gemini Live, or Groq LPU. Zero shared secrets.")),
                _ => (L("Paso 3 de 3 · Prueba Rápida de Voz y Overlay", "Step 3 of 3 · Voice & Overlay Check"),
                      L("Confirmá tu audio y mirá cómo se ve el cartel flotante sin consumir VRAM de tu placa de video.", "Test mic levels and preview the floating click-through HUD with 0% GPU impact."))
            };
        }

        btnOr.Click += async (_, _) =>
        {
            btnOr.IsEnabled = false;
            lblAiStatus.Text = L("⏳ Esperando autorización en el navegador...", "⏳ Waiting for browser sign-in...");
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
                lblAiStatus.Text = L("✅ Google Gemini Live conectado correctamente.", "✅ Google Gemini Live connected successfully.");
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
            lblAiStatus.Text = L("✅ Clave de Groq guardada de forma segura con DPAPI.", "✅ Groq API Key sealed with Windows DPAPI.");
            lblAiStatus.Foreground = FreezeBrush("#34D399");
        };

        btnTestMic.Click += async (_, _) =>
        {
            btnTestMic.IsEnabled = false;
            lblMicResult.Text = L("🎙 Hablá normal durante 1 segundo...", "🎙 Speak normally for 1 second...");
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
                lblMicResult.Text = L($"⚠ Error al probar micrófono: {ex.Message}", $"⚠ Mic test failed: {ex.Message}");
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
