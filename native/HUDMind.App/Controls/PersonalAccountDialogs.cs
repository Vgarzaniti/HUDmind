using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HUDMind.App.Services;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaBrushes = System.Windows.Media.Brushes;

namespace HUDMind.App.Controls;

public enum PersonalProfileDialogOutcome
{
    None,
    SwitchProfile,
    UpdatedActiveMetadata,
    PinChanged,
    ExportArchive,
    ImportArchive,
    LockSession
}

public sealed record PersonalProfileDialogResult(
    PersonalProfileDialogOutcome Outcome,
    UserProfileRecord? TargetProfile = null,
    string StatusMessage = "",
    bool CopySettingsFromCurrent = false);

/// <summary>
/// Pure-WPF 0% GPU dark-theme dialogs for personal local profiles, PBKDF2 PIN unlock,
/// portable AES-256-GCM (.hudmind) passphrase prompts, guided Gemini onboarding,
/// system health diagnostics, crash recovery, and the first-run setup wizard.
/// </summary>
public static partial class PersonalAccountDialogs
{
    private static readonly SolidColorBrush BgWindow = FreezeBrush("#070C13");
    private static readonly SolidColorBrush BgCard = FreezeBrush("#0B131E");
    private static readonly SolidColorBrush BgInput = FreezeBrush("#0F1A28");
    private static readonly SolidColorBrush BorderSubtle = FreezeBrush("#1B2E43");
    private static readonly SolidColorBrush TextPrimary = FreezeBrush("#F1F5F9");
    private static readonly SolidColorBrush TextMuted = FreezeBrush("#8EA4BC");
    private static readonly SolidColorBrush TextAccent = FreezeBrush("#22D3EE");
    private static readonly SolidColorBrush TextDanger = FreezeBrush("#FCA5A5");
    private static readonly SolidColorBrush BtnPrimaryBg = FreezeBrush("#086F83");
    private static readonly SolidColorBrush BtnPrimaryBorder = FreezeBrush("#22D3EE");
    private static readonly SolidColorBrush BtnNeutralBg = FreezeBrush("#111D2C");
    private static readonly SolidColorBrush BtnDangerBg = FreezeBrush("#7F1D1D");
    private static readonly SolidColorBrush BtnDangerBorder = FreezeBrush("#EF4444");

    public static readonly string[] AvatarChoices = ["🎮", "⚡", "🔥", "🦊", "🦅", "🎯", "🏆", "🛡️"];
    public static readonly (string Name, string Hex)[] AccentChoices =
    [
        ("Cyan", "#22D3EE"),
        ("Esmeralda", "#10B981"),
        ("Violeta", "#A78BFA"),
        ("Ámbar", "#F59E0B"),
        ("Rosa", "#F472B6"),
        ("Azul", "#38BDF8")
    ];

    private static string L(string es, string en) => AppLocalizationService.T(es, en);

    public static (bool Unlocked, UserProfileRecord? SwitchToProfile) PromptPinUnlock(
        Window? owner,
        UserProfileStore store,
        UserProfileRecord profile,
        bool allowSwitchProfile = true)
    {
        bool unlocked = false;
        UserProfileRecord? switchTo = null;

        var win = CreateModalWindow(
            owner,
            L($"HUDMind · Desbloquear perfil ({profile.DisplayName})", $"HUDMind · Unlock Profile ({profile.DisplayName})"),
            width: 410,
            height: 290);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"{profile.AvatarBadge}  {profile.DisplayName}",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = ParseHexBrush(profile.AccentHex)
        });
        root.Children.Add(new TextBlock
        {
            Text = L($"ID único: {profile.UniqueUserId}", $"Unique ID: {profile.UniqueUserId}"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = FreezeBrush("#67E8F9"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        root.Children.Add(new TextBlock
        {
            Text = L("Ingresá tu PIN personal para cargar tus configuraciones, reglas y claves privadas.", "Enter your personal PIN to unlock your settings, rules, and private API keys."),
            Foreground = TextMuted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });

        var pwd = CreatePasswordBox();
        root.Children.Add(pwd);

        var err = new TextBlock
        {
            Foreground = TextDanger,
            FontSize = 11.5,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(err);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        if (allowSwitchProfile)
        {
            var btnSwitch = CreateButton(L("🔄 Otro perfil", "🔄 Switch profile"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
            btnSwitch.Click += (_, _) =>
            {
                var idx = store.LoadOrInitialize();
                var openAlternative = idx.Profiles.FirstOrDefault(p =>
                    !p.ProfileId.Equals(profile.ProfileId, StringComparison.OrdinalIgnoreCase) && !p.HasPin);
                if (openAlternative is not null)
                {
                    switchTo = store.SwitchActiveProfile(openAlternative.ProfileId);
                    win.DialogResult = true;
                    win.Close();
                }
                else
                {
                    err.Text = L("Todos los demás perfiles también tienen PIN o no hay otros perfiles abiertos.", "All other profiles also require a PIN or no unlocked profiles exist.");
                    err.Visibility = Visibility.Visible;
                }
            };
            buttons.Children.Add(btnSwitch);
        }

        var btnCancel = CreateButton(L("Salir", "Exit"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        btnCancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        buttons.Children.Add(btnCancel);

        var btnUnlock = CreateButton(L("🔓 Entrar", "🔓 Unlock"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        void TryUnlock()
        {
            if (UserProfileStore.VerifyPin(profile.PinHash, pwd.Password))
            {
                unlocked = true;
                win.DialogResult = true;
                win.Close();
            }
            else
            {
                err.Text = L("PIN incorrecto. Volvé a intentarlo.", "Incorrect PIN. Please try again.");
                err.Visibility = Visibility.Visible;
                pwd.SelectAll();
                pwd.Focus();
            }
        }
        btnUnlock.Click += (_, _) => TryUnlock();
        pwd.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TryUnlock();
            }
        };
        buttons.Children.Add(btnUnlock);

        root.Children.Add(buttons);
        win.Content = root;
        win.Loaded += (_, _) => pwd.Focus();
        win.ShowDialog();
        return (unlocked, switchTo);
    }

    public static PersonalProfileDialogResult ShowProfileManagerDialog(
        Window? owner,
        UserProfileStore store,
        UserProfileRecord activeProfile,
        IReadOnlyList<GameProfile> games)
    {
        var result = new PersonalProfileDialogResult(PersonalProfileDialogOutcome.None);
        var win = CreateModalWindow(owner, L("HUDMind · Mi Cuenta y Perfiles Locales ($0 Privado)", "HUDMind · Local Accounts & Profiles ($0 Private)"), width: 730, height: 625);
        AutomationProperties.SetAutomationId(win, "HUDMindProfileManagerDialog");

        var mainGrid = new Grid { Margin = new Thickness(16) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = L("👤 PERFILES PERSONALES (100% EN TU PC · SIN NUBE AJENA)", "👤 PERSONAL PROFILES (100% ON-DEVICE · ZERO CLOUD SERVER)"),
            Foreground = TextAccent,
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });
        header.Children.Add(new TextBlock
        {
            Text = L(
                "Cada perfil guarda aparte sus claves IA, reglas de juego, voz, idioma, memoria y contador de fugas. Si compartís PC podés proteger el tuyo con PIN.",
                "Each profile isolates its AI keys, custom rules, voice, language, match memory, and leak counters. Protect yours with an optional PIN on shared PCs."),
            Foreground = TextMuted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetRow(header, 0);
        mainGrid.Children.Add(header);

        var midGrid = new Grid();
        midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        Grid.SetRow(midGrid, 1);
        mainGrid.Children.Add(midGrid);

        // Left: profile selector + switch/pin/delete
        var leftCard = CreateCardBorder();
        var leftDock = new DockPanel();
        var listTitle = new TextBlock
        {
            Text = L("Cuentas en este equipo:", "Profiles on this PC:"),
            Foreground = TextPrimary,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(listTitle, Dock.Top);
        leftDock.Children.Add(listTitle);

        var profileList = new ListBox
        {
            Background = BgInput,
            Foreground = TextPrimary,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        void ReloadProfilesList(string? selectId = null)
        {
            var idx = store.LoadOrInitialize();
            profileList.Items.Clear();
            foreach (var p in idx.Profiles)
            {
                bool isCurrent = p.ProfileId.Equals(activeProfile.ProfileId, StringComparison.OrdinalIgnoreCase);
                string pinTag = p.HasPin ? " 🔒" : "";
                string activeTag = isCurrent ? L(" · ACTIVO", " · ACTIVE") : "";
                string fav = string.IsNullOrWhiteSpace(p.FavoriteGameId) ? "" : $" [{p.FavoriteGameId}]";
                var item = new ListBoxItem
                {
                    Tag = p,
                    Content = $"{p.AvatarBadge}  {p.DisplayName}{pinTag}{fav}{activeTag}  ·  [{p.UniqueUserId}]",
                    Foreground = isCurrent ? ParseHexBrush(p.AccentHex) : TextPrimary,
                    FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                    Padding = new Thickness(6, 5, 6, 5)
                };
                profileList.Items.Add(item);
                if (selectId is not null && p.ProfileId.Equals(selectId, StringComparison.OrdinalIgnoreCase))
                    profileList.SelectedItem = item;
                else if (selectId is null && isCurrent)
                    profileList.SelectedItem = item;
            }
            if (profileList.SelectedIndex < 0 && profileList.Items.Count > 0)
                profileList.SelectedIndex = 0;
        }

        var selectedInfo = new TextBlock
        {
            Text = L($"Seleccionado: {activeProfile.AvatarBadge} {activeProfile.DisplayName} · [{activeProfile.UniqueUserId}]", $"Selected: {activeProfile.AvatarBadge} {activeProfile.DisplayName} · [{activeProfile.UniqueUserId}]"),
            Foreground = TextMuted,
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 2)
        };

        var leftActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(leftActions, Dock.Bottom);
        DockPanel.SetDock(selectedInfo, Dock.Bottom);
        var btnEnterProfile = CreateButton(L("✅ Entrar / Activar", "✅ Switch / Activate"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 6, 6));
        var btnTogglePin = CreateButton(L("🔒 Activar PIN", "🔒 Set PIN"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 6, 6));
        var btnDeleteProfile = CreateButton(L("🗑 Eliminar", "🗑 Delete"), BtnDangerBg, BtnDangerBorder, new Thickness(0, 0, 0, 6));
        leftActions.Children.Add(btnEnterProfile);
        leftActions.Children.Add(btnTogglePin);
        leftActions.Children.Add(btnDeleteProfile);
        leftDock.Children.Add(leftActions);
        leftDock.Children.Add(selectedInfo);
        leftDock.Children.Add(profileList);
        leftCard.Child = leftDock;
        Grid.SetColumn(leftCard, 0);
        midGrid.Children.Add(leftCard);

        // Right: Edit Active or Create New Profile
        var rightCard = CreateCardBorder();
        var rightStack = new StackPanel();
        var rightTitle = new TextBlock
        {
            Text = L("✨ CREAR NUEVA CUENTA / PERSONALIZAR ACTIVA", "✨ CREATE NEW PROFILE / CUSTOMIZE ACTIVE"),
            Foreground = TextAccent,
            FontWeight = FontWeights.Bold,
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 8)
        };
        rightStack.Children.Add(rightTitle);

        rightStack.Children.Add(CreateFieldLabel(L("Identificador único permanente (irrepetible):", "Permanent Unique ID (non-repeating):")));
        var idRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        idRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        idRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        idRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var txtUniqueId = CreateTextBox(activeProfile.UniqueUserId);
        AutomationProperties.SetAutomationId(txtUniqueId, "TxtProfileManagerUniqueId");
        txtUniqueId.IsReadOnly = true;
        txtUniqueId.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        txtUniqueId.Foreground = FreezeBrush("#67E8F9");
        var btnCopyId = CreateButton(L("📋 Copiar", "📋 Copy"), BtnNeutralBg, BorderSubtle, new Thickness(0));
        btnCopyId.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(activeProfile.UniqueUserId);
                btnCopyId.Content = L("✅ Copiado", "✅ Copied");
            }
            catch { }
        };
        Grid.SetColumn(txtUniqueId, 0);
        Grid.SetColumn(btnCopyId, 2);
        idRow.Children.Add(txtUniqueId);
        idRow.Children.Add(btnCopyId);
        rightStack.Children.Add(idRow);

        rightStack.Children.Add(CreateFieldLabel(L("Nombre / Nickname:", "Display Name / Nickname:")));
        var txtName = CreateTextBox(activeProfile.DisplayName);
        AutomationProperties.SetAutomationId(txtName, "TxtProfileManagerName");
        rightStack.Children.Add(txtName);

        var avatarAccentRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        avatarAccentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        avatarAccentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        avatarAccentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

        var avatarCol = new StackPanel();
        avatarCol.Children.Add(CreateFieldLabel(L("Avatar:", "Badge:")));
        var cmbAvatar = new ComboBox { Background = BgInput, Foreground = TextPrimary, BorderBrush = BorderSubtle };
        foreach (var emoji in AvatarChoices)
            cmbAvatar.Items.Add(new ComboBoxItem { Content = emoji, Tag = emoji });
        SelectComboTag(cmbAvatar, activeProfile.AvatarBadge);
        avatarCol.Children.Add(cmbAvatar);
        Grid.SetColumn(avatarCol, 0);
        avatarAccentRow.Children.Add(avatarCol);

        var accentCol = new StackPanel();
        accentCol.Children.Add(CreateFieldLabel(L("Color personal:", "Accent Color:")));
        var cmbAccent = new ComboBox { Background = BgInput, Foreground = TextPrimary, BorderBrush = BorderSubtle };
        foreach (var (name, hex) in AccentChoices)
            cmbAccent.Items.Add(new ComboBoxItem { Content = name, Tag = hex });
        SelectComboTag(cmbAccent, activeProfile.AccentHex);
        accentCol.Children.Add(cmbAccent);
        Grid.SetColumn(accentCol, 2);
        avatarAccentRow.Children.Add(accentCol);
        rightStack.Children.Add(avatarAccentRow);

        rightStack.Children.Add(CreateFieldLabel(L("Juego principal favorito:", "Favorite Primary Game:"), topMargin: 8));
        var cmbFavGame = new ComboBox { Background = BgInput, Foreground = TextPrimary, BorderBrush = BorderSubtle };
        cmbFavGame.Items.Add(new ComboBoxItem { Content = L("🌐 Automático (Cualquiera)", "🌐 Automatic (Any Game)"), Tag = "" });
        foreach (var g in games.Where(x => !string.Equals(x.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase)))
            cmbFavGame.Items.Add(new ComboBoxItem { Content = g.Manifest.DisplayName, Tag = g.Manifest.GameId });
        SelectComboTag(cmbFavGame, activeProfile.FavoriteGameId);
        rightStack.Children.Add(cmbFavGame);

        rightStack.Children.Add(CreateFieldLabel(L("PIN opcional al crear (vacío = entrada directa):", "Optional PIN (blank = instant login):"), topMargin: 8));
        var pwdNewPin = CreatePasswordBox();
        rightStack.Children.Add(pwdNewPin);

        var rightError = new TextBlock
        {
            Foreground = TextDanger,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        rightStack.Children.Add(rightError);

        var chkCopyFromCurrent = new CheckBox
        {
            Content = L($"Copiar claves IA y ajustes de '{activeProfile.DisplayName}' al crear", $"Copy AI keys & settings from '{activeProfile.DisplayName}' on create"),
            Foreground = TextMuted,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            IsChecked = false
        };
        rightStack.Children.Add(chkCopyFromCurrent);

        var formBtns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var btnSaveActive = CreateButton(L("💾 Guardar en perfil activo", "💾 Save Active Profile"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 6, 6));
        var btnCreateNew = CreateButton(L("➕ Crear y Entrar como Nueva Cuenta", "➕ Create & Switch to New Profile"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(btnCreateNew, "BtnProfileManagerCreate");
        formBtns.Children.Add(btnSaveActive);
        formBtns.Children.Add(btnCreateNew);
        rightStack.Children.Add(formBtns);

        rightCard.Child = rightStack;
        Grid.SetColumn(rightCard, 2);
        midGrid.Children.Add(rightCard);

        // Bottom portable backup strip
        var backupCard = CreateCardBorder();
        backupCard.Margin = new Thickness(0, 10, 0, 0);
        var backupDock = new DockPanel();
        var backupBtns = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(backupBtns, Dock.Right);
        var btnExport = CreateButton(L("📦 Exportar .hudmind", "📦 Export .hudmind"), BtnNeutralBg, BorderSubtle, new Thickness(8, 0, 6, 0));
        var btnImport = CreateButton(L("📥 Importar .hudmind", "📥 Import .hudmind"), BtnNeutralBg, BorderSubtle, new Thickness(0));
        backupBtns.Children.Add(btnExport);
        backupBtns.Children.Add(btnImport);
        backupDock.Children.Add(backupBtns);
        backupDock.Children.Add(new TextBlock
        {
            Text = L(
                "💾 Llevá tu cuenta a otra PC ($0 sin servidor): archivo cifrado AES-256-GCM con tus preferencias, reglas y memoria.",
                "💾 Carry your account across PCs ($0 serverless): AES-256-GCM encrypted bundle containing your preferences, rules, and match memory."),
            Foreground = TextMuted,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        backupCard.Child = backupDock;
        Grid.SetRow(backupCard, 2);
        mainGrid.Children.Add(backupCard);

        var bottomClose = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        if (activeProfile.HasPin)
        {
            var btnLockNow = CreateButton(L("🔒 Bloquear sesión", "🔒 Lock Session"), BtnNeutralBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
            btnLockNow.Click += (_, _) =>
            {
                result = new PersonalProfileDialogResult(PersonalProfileDialogOutcome.LockSession, activeProfile);
                win.Close();
            };
            bottomClose.Children.Add(btnLockNow);
        }
        var btnClose = CreateButton(L("Cerrar", "Close"), BtnNeutralBg, BorderSubtle, new Thickness(0));
        AutomationProperties.SetAutomationId(btnClose, "BtnProfileManagerClose");
        btnClose.Click += (_, _) => win.Close();
        bottomClose.Children.Add(btnClose);
        Grid.SetRow(bottomClose, 3);
        mainGrid.Children.Add(bottomClose);

        // Wire handlers
        UserProfileRecord? SelectedRecord() => (profileList.SelectedItem as ListBoxItem)?.Tag as UserProfileRecord;

        void UpdateSelectionUi()
        {
            var sel = SelectedRecord();
            if (sel is null)
            {
                selectedInfo.Text = L("Seleccioná una cuenta", "Select a profile");
                btnDeleteProfile.IsEnabled = false;
                return;
            }
            selectedInfo.Text = L($"Seleccionado: {sel.AvatarBadge} {sel.DisplayName} · [{sel.UniqueUserId}]", $"Selected: {sel.AvatarBadge} {sel.DisplayName} · [{sel.UniqueUserId}]");
            btnDeleteProfile.IsEnabled = !sel.ProfileId.Equals("default", StringComparison.OrdinalIgnoreCase);
            btnTogglePin.Content = sel.HasPin ? L("🔓 Quitar PIN", "🔓 Remove PIN") : L("🔒 Activar PIN", "🔒 Set PIN");
        }

        void ActivateSelectedProfile()
        {
            var sel = SelectedRecord();
            if (sel is null) return;
            if (sel.ProfileId.Equals(activeProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
            {
                win.Close();
                return;
            }
            if (sel.HasPin)
            {
                var (ok, _) = PromptPinUnlock(win, store, sel, allowSwitchProfile: false);
                if (!ok) return;
            }
            var switched = store.SwitchActiveProfile(sel.ProfileId);
            result = new PersonalProfileDialogResult(
                PersonalProfileDialogOutcome.SwitchProfile,
                switched,
                L($"Sesión cambiada a {switched.AvatarBadge} {switched.DisplayName}.", $"Switched active profile to {switched.AvatarBadge} {switched.DisplayName}."));
            win.Close();
        }

        profileList.SelectionChanged += (_, _) => UpdateSelectionUi();
        profileList.MouseDoubleClick += (_, _) => ActivateSelectedProfile();

        ReloadProfilesList();
        UpdateSelectionUi();

        btnEnterProfile.Click += (_, _) => ActivateSelectedProfile();

        btnTogglePin.Click += (_, _) =>
        {
            var sel = SelectedRecord();
            if (sel is null) return;
            if (sel.HasPin)
            {
                var (ok, _) = PromptPinUnlock(win, store, sel, allowSwitchProfile: false);
                if (!ok) return;
                store.SetProfilePin(sel.ProfileId, null);
                ReloadProfilesList(sel.ProfileId);
                result = new PersonalProfileDialogResult(
                    PersonalProfileDialogOutcome.PinChanged,
                    sel,
                    L($"🔓 PIN removido de '{sel.DisplayName}'.", $"🔓 PIN removed from '{sel.DisplayName}'."));
            }
            else
            {
                var pin = pwdNewPin.Password.Trim();
                if (pin.Length < 4 || pin.Length > 16)
                {
                    rightError.Text = L("Escribí arriba un PIN de 4 a 16 caracteres en 'PIN opcional' y luego tocá '🔒 Activar PIN'.", "Enter a 4–16 character PIN in 'Optional PIN' above, then click '🔒 Set PIN'.");
                    rightError.Visibility = Visibility.Visible;
                    pwdNewPin.Focus();
                    return;
                }
                store.SetProfilePin(sel.ProfileId, pin);
                pwdNewPin.Clear();
                rightError.Visibility = Visibility.Collapsed;
                ReloadProfilesList(sel.ProfileId);
                result = new PersonalProfileDialogResult(
                    PersonalProfileDialogOutcome.PinChanged,
                    sel,
                    L($"🔒 PIN activado en '{sel.DisplayName}'.", $"🔒 PIN enabled for '{sel.DisplayName}'."));
            }
        };

        btnDeleteProfile.Click += (_, _) =>
        {
            var sel = SelectedRecord();
            if (sel is null) return;
            if (sel.ProfileId.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show(L("El perfil base 'default' no se puede eliminar (podés renombrarlo a tu gusto).", "The base 'default' profile cannot be deleted (you can rename it anytime)."), "HUDMind", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (sel.HasPin)
            {
                var (ok, _) = PromptPinUnlock(win, store, sel, allowSwitchProfile: false);
                if (!ok) return;
            }
            var confirm = System.Windows.MessageBox.Show(
                L($"¿Eliminar el perfil '{sel.DisplayName}' y sus archivos locales? Esta acción no se puede deshacer.", $"Delete profile '{sel.DisplayName}' and its local workspace files? This cannot be undone."),
                L("HUDMind · Eliminar perfil", "HUDMind · Delete Profile"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            store.DeleteProfile(sel.ProfileId);
            var refreshedIdx = store.LoadOrInitialize();
            var fallback = refreshedIdx.Profiles.First(p => p.ProfileId == refreshedIdx.ActiveProfileId);
            result = new PersonalProfileDialogResult(
                PersonalProfileDialogOutcome.SwitchProfile,
                fallback,
                L($"Perfil '{sel.DisplayName}' eliminado. Activo: {fallback.DisplayName}.", $"Profile '{sel.DisplayName}' deleted. Active: {fallback.DisplayName}."));
            win.Close();
        };

        btnSaveActive.Click += (_, _) =>
        {
            var name = txtName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                rightError.Text = L("Ingresá un nombre para tu perfil.", "Enter a profile name.");
                rightError.Visibility = Visibility.Visible;
                return;
            }
            var avatar = GetComboTag(cmbAvatar, "🎮");
            var accent = GetComboTag(cmbAccent, "#22D3EE");
            var fav = GetComboTag(cmbFavGame, "");
            store.UpdateProfileMetadata(activeProfile.ProfileId, name, avatar, accent, fav);
            var refreshed = store.LoadOrInitialize().Profiles.First(p => p.ProfileId == activeProfile.ProfileId);
            result = new PersonalProfileDialogResult(
                PersonalProfileDialogOutcome.UpdatedActiveMetadata,
                refreshed,
                L($"Perfil personalizado: {refreshed.AvatarBadge} {refreshed.DisplayName}.", $"Profile updated: {refreshed.AvatarBadge} {refreshed.DisplayName}."));
            win.Close();
        };

        btnCreateNew.Click += (_, _) =>
        {
            var name = txtName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                rightError.Text = L("Escribí un nombre para tu nueva cuenta.", "Enter a name for your new profile.");
                rightError.Visibility = Visibility.Visible;
                txtName.Focus();
                return;
            }
            if (string.Equals(name, activeProfile.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                rightError.Text = L("Escribí un nombre distinto al perfil actual para crear una cuenta nueva.", "Enter a different name from the current profile to create a new one.");
                rightError.Visibility = Visibility.Visible;
                txtName.SelectAll();
                txtName.Focus();
                return;
            }
            var pin = pwdNewPin.Password.Trim();
            if (!string.IsNullOrEmpty(pin) && (pin.Length < 4 || pin.Length > 16))
            {
                rightError.Text = L("El PIN debe tener entre 4 y 16 caracteres (o dejalo vacío).", "PIN must be 4–16 characters (or leave blank).");
                rightError.Visibility = Visibility.Visible;
                return;
            }

            var created = store.CreateProfile(
                name,
                GetComboTag(cmbAvatar, "🎮"),
                GetComboTag(cmbAccent, "#22D3EE"),
                GetComboTag(cmbFavGame, ""),
                string.IsNullOrEmpty(pin) ? null : pin);
            result = new PersonalProfileDialogResult(
                PersonalProfileDialogOutcome.SwitchProfile,
                created,
                L($"Cuenta personal creada: {created.AvatarBadge} {created.DisplayName}.", $"Created local profile: {created.AvatarBadge} {created.DisplayName}."),
                CopySettingsFromCurrent: chkCopyFromCurrent.IsChecked == true);
            win.Close();
        };

        btnExport.Click += (_, _) =>
        {
            result = new PersonalProfileDialogResult(PersonalProfileDialogOutcome.ExportArchive, activeProfile);
            win.Close();
        };

        btnImport.Click += (_, _) =>
        {
            result = new PersonalProfileDialogResult(PersonalProfileDialogOutcome.ImportArchive, activeProfile);
            win.Close();
        };

        win.Content = mainGrid;
        win.ShowDialog();
        return result;
    }

    public static PersonalProfileDialogOutcome ShowQuickBackupDialog(Window? owner, UserProfileRecord activeProfile)
    {
        var chosen = PersonalProfileDialogOutcome.None;
        var win = CreateModalWindow(owner, L("HUDMind · Backup Portable Cifrado (.hudmind)", "HUDMind · Encrypted Portable Backup (.hudmind)"), width: 455, height: 215);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"{activeProfile.AvatarBadge}  {activeProfile.DisplayName}  ·  {activeProfile.UniqueUserId}",
            FontWeight = FontWeights.Bold,
            FontSize = 13.5,
            Foreground = ParseHexBrush(activeProfile.AccentHex)
        });
        root.Children.Add(new TextBlock
        {
            Text = L(
                "Guardá o restaurá preferencias, reglas por juego, memoria y seguimiento de fugas en un archivo AES-256-GCM ($0 sin servidor).",
                "Save or restore preferences, per-game rules, match memory, and leak trackers inside an AES-256-GCM encrypted file ($0 serverless)."),
            Foreground = TextMuted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 14)
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnExport = CreateButton(L("📦 Exportar .hudmind", "📦 Export .hudmind"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnImport = CreateButton(L("📥 Importar .hudmind", "📥 Import .hudmind"), BtnNeutralBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnCancel = CreateButton(L("Cancelar", "Cancel"), BtnNeutralBg, BorderSubtle, new Thickness(0));
        btnExport.Click += (_, _) => { chosen = PersonalProfileDialogOutcome.ExportArchive; win.Close(); };
        btnImport.Click += (_, _) => { chosen = PersonalProfileDialogOutcome.ImportArchive; win.Close(); };
        btnCancel.Click += (_, _) => { chosen = PersonalProfileDialogOutcome.None; win.Close(); };
        actions.Children.Add(btnExport);
        actions.Children.Add(btnImport);
        actions.Children.Add(btnCancel);
        root.Children.Add(actions);

        win.Content = root;
        win.ShowDialog();
        return chosen;
    }

    public static (bool Confirmed, string Passphrase, bool IncludeApiAccounts) ShowPassphraseDialog(
        Window? owner,
        bool isExport)
    {
        bool confirmed = false;
        string phrase = string.Empty;
        bool includeKeys = false;

        var win = CreateModalWindow(
            owner,
            isExport ? L("HUDMind · Cifrar Backup (.hudmind)", "HUDMind · Encrypt Backup (.hudmind)") : L("HUDMind · Descifrar Backup (.hudmind)", "HUDMind · Decrypt Backup (.hudmind)"),
            width: 460,
            height: 290);
        AutomationProperties.SetAutomationId(win, "HUDMindPassphraseDialog");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = isExport
                ? L("Elegí una contraseña para cifrar tu archivo personal AES-256-GCM (mínimo 4 caracteres).", "Choose a passphrase to encrypt your AES-256-GCM backup file (min. 4 characters).")
                : L("Ingresá la contraseña con la que cifraste este archivo .hudmind.", "Enter the passphrase used to encrypt this .hudmind backup."),
            Foreground = TextMuted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var pwd1 = CreatePasswordBox();
        AutomationProperties.SetAutomationId(pwd1, "PwdArchivePassphrase");
        root.Children.Add(pwd1);

        var chkIncludeKeys = new CheckBox
        {
            Content = isExport
                ? L("Incluir cuentas IA (solo útil para restaurar en el mismo Windows/PC)", "Include encrypted AI accounts (for restoring on the same Windows PC)")
                : L("Restaurar también cuentas IA incluidas en este backup (mismo Windows)", "Also restore bundled AI accounts (same Windows user)"),
            Foreground = TextMuted,
            FontSize = 11.5,
            Margin = new Thickness(0, 10, 0, 0),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(chkIncludeKeys, "ChkArchiveIncludeKeys");
        root.Children.Add(chkIncludeKeys);

        var err = new TextBlock
        {
            Foreground = TextDanger,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(err);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = CreateButton(L("Cancelar", "Cancel"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        AutomationProperties.SetAutomationId(cancel, "BtnArchivePassphraseCancel");
        cancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        var ok = CreateButton(isExport ? L("📦 Exportar", "📦 Export") : L("📥 Importar", "📥 Import"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        AutomationProperties.SetAutomationId(ok, "BtnArchivePassphraseOk");
        void TrySubmit()
        {
            if (string.IsNullOrWhiteSpace(pwd1.Password) || pwd1.Password.Length < 4)
            {
                err.Text = L("La contraseña debe tener al menos 4 caracteres.", "Passphrase must be at least 4 characters long.");
                err.Visibility = Visibility.Visible;
                return;
            }
            confirmed = true;
            phrase = pwd1.Password;
            includeKeys = chkIncludeKeys.IsChecked == true;
            win.DialogResult = true;
            win.Close();
        }
        ok.Click += (_, _) => TrySubmit();
        pwd1.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TrySubmit();
            }
        };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        root.Children.Add(btns);

        win.Content = root;
        win.Loaded += (_, _) => pwd1.Focus();
        win.ShowDialog();
        return (confirmed, phrase, includeKeys);
    }

    public static (bool Confirmed, string ApiKey, string Label) ShowGuidedGeminiKeyDialog(
        Window? owner,
        Action openAiStudioAction)
    {
        bool confirmed = false;
        string finalKey = string.Empty;
        string finalLabel = L("Mi Gemini Live (Personal)", "My Gemini Live (Personal)");

        var win = CreateModalWindow(owner, L("HUDMind · Conectar Tu Cuenta Google AI Studio ($0)", "HUDMind · Connect Google AI Studio ($0)"), width: 510, height: 350);
        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = L("✨ CONECTÁ TU GEMINI EN 2 CLICS (VOZ LIVE + VISIÓN)", "✨ CONNECT GEMINI IN 2 CLICKS (LIVE VOICE + VISION)"),
            Foreground = TextAccent,
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });
        root.Children.Add(new TextBlock
        {
            Text = L(
                "Google no permite crear claves desde apps externas sin consola Cloud. Con este asistente no tocás código y tu clave queda solo en tu Windows (DPAPI):",
                "Google requires creating API keys directly in Google AI Studio. Follow these 2 clicks—no coding required, sealed locally via Windows DPAPI:"),
            Foreground = TextMuted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var btnStep1 = CreateButton(L("1. 🌐 Abrir Google AI Studio", "1. 🌐 Open Google AI Studio"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0, 0, 8, 0));
        var btnStep2 = CreateButton(L("2. 📋 Pegar clave copiada (AIza...)", "2. 📋 Paste Copied Key (AIza...)"), BtnNeutralBg, BtnPrimaryBorder, new Thickness(0));
        stepRow.Children.Add(btnStep1);
        stepRow.Children.Add(btnStep2);
        root.Children.Add(stepRow);

        root.Children.Add(new TextBlock
        {
            Text = L("Tip: En la web iniciá sesión con tu Gmail, tocá 'Create API key', tocá 'Copy' y después apretá el botón 2 acá.", "Tip: Sign in with Gmail, click 'Create API key', copy it, then click Step 2 above."),
            Foreground = TextMuted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        root.Children.Add(CreateFieldLabel(L("Nombre para mostrar en tu perfil:", "Label in your profile:")));
        var txtLabel = CreateTextBox(finalLabel);
        root.Children.Add(txtLabel);

        root.Children.Add(CreateFieldLabel(L("Tu Clave Gemini (oculta con puntos para OBS):", "Your Gemini Key (masked for OBS safety):"), topMargin: 8));
        var pwdKey = CreatePasswordBox();
        root.Children.Add(pwdKey);

        var statusTxt = new TextBlock
        {
            Foreground = TextAccent,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(statusTxt);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var btnCancel = CreateButton(L("Cancelar", "Cancel"), BtnNeutralBg, BorderSubtle, new Thickness(0, 0, 8, 0));
        var btnSave = CreateButton(L("✅ Guardar y Activar Gemini", "✅ Save & Activate Gemini"), BtnPrimaryBg, BtnPrimaryBorder, new Thickness(0));
        bottom.Children.Add(btnCancel);
        bottom.Children.Add(btnSave);
        root.Children.Add(bottom);

        btnStep1.Click += (_, _) =>
        {
            openAiStudioAction();
            statusTxt.Text = L("🌐 Abrimos Google AI Studio en tu navegador. Copiá tu clave 'AIza...' y tocá el botón 2.", "🌐 Opened Google AI Studio in your browser. Copy your 'AIza...' key and click Step 2.");
            statusTxt.Foreground = TextAccent;
            statusTxt.Visibility = Visibility.Visible;
        };

        btnStep2.Click += (_, _) =>
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    statusTxt.Text = L("⚠ No hay texto copiado en tu portapapeles todavía.", "⚠ Your clipboard is currently empty.");
                    statusTxt.Foreground = TextDanger;
                    statusTxt.Visibility = Visibility.Visible;
                    return;
                }
                var clip = Clipboard.GetText().Trim();
                if (!clip.StartsWith("AIza", StringComparison.Ordinal) || clip.Length < 25)
                {
                    statusTxt.Text = L("⚠ Lo que está en tu portapapeles no parece una clave de Gemini (empiezan con 'AIza...'). Podés pegarla manual abajo si querés.", "⚠ Clipboard text doesn't look like a Gemini key ('AIza...'). You can paste it manually below.");
                    statusTxt.Foreground = TextDanger;
                    statusTxt.Visibility = Visibility.Visible;
                    return;
                }
                pwdKey.Password = clip;
                statusTxt.Text = L($"✅ Clave detectada y pegada en forma segura ({clip[..8]}…{clip[^4..]}). Tocá Guardar.", $"✅ Key detected & pasted safely ({clip[..8]}…{clip[^4..]}). Click Save.");
                statusTxt.Foreground = FreezeBrush("#6EE7B7");
                statusTxt.Visibility = Visibility.Visible;
            }
            catch
            {
                statusTxt.Text = L("Pegala manualmente con Ctrl+V dentro del casillero de clave.", "Paste manually using Ctrl+V in the key box.");
                statusTxt.Foreground = TextDanger;
                statusTxt.Visibility = Visibility.Visible;
            }
        };

        btnCancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        btnSave.Click += (_, _) =>
        {
            var key = pwdKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Length < 20)
            {
                statusTxt.Text = L("Pegá tu API Key de Gemini antes de guardar.", "Paste your Gemini API Key before saving.");
                statusTxt.Foreground = TextDanger;
                statusTxt.Visibility = Visibility.Visible;
                return;
            }
            confirmed = true;
            finalKey = key;
            finalLabel = string.IsNullOrWhiteSpace(txtLabel.Text) ? L("Mi Gemini Live (Personal)", "My Gemini Live (Personal)") : txtLabel.Text.Trim();
            win.DialogResult = true;
            win.Close();
        };

        win.Content = root;
        win.ShowDialog();
        return (confirmed, finalKey, finalLabel);
    }

    private static Window CreateModalWindow(Window? owner, string title, double width, double height) => new()
    {
        Owner = owner,
        Title = title,
        Width = width,
        Height = height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        Background = BgWindow,
        Foreground = TextPrimary
    };

    private static Border CreateCardBorder() => new()
    {
        Background = BgCard,
        BorderBrush = BorderSubtle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(11)
    };

    private static TextBlock CreateFieldLabel(string text, double topMargin = 0) => new()
    {
        Text = text,
        Foreground = TextMuted,
        FontSize = 11,
        Margin = new Thickness(0, topMargin, 0, 3)
    };

    private static TextBox CreateTextBox(string initial) => new()
    {
        Text = initial,
        Background = BgInput,
        Foreground = TextPrimary,
        CaretBrush = TextAccent,
        BorderBrush = BorderSubtle,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 5, 8, 5)
    };

    private static PasswordBox CreatePasswordBox() => new()
    {
        Background = BgInput,
        Foreground = TextPrimary,
        CaretBrush = TextAccent,
        BorderBrush = BorderSubtle,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 5, 8, 5)
    };

    private static Button CreateButton(string text, SolidColorBrush bg, SolidColorBrush border, Thickness margin) => new()
    {
        Content = text,
        Background = bg,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        Foreground = TextPrimary,
        Padding = new Thickness(10, 5, 10, 5),
        Margin = margin,
        Cursor = Cursors.Hand
    };

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var b = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static SolidColorBrush ParseHexBrush(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return FreezeBrush(hex.Trim());
        }
        catch { }
        return TextAccent;
    }

    private static void SelectComboTag(ComboBox combo, string? tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string GetComboTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
}
