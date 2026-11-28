using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Security;
using HUDMind.Windows.Security;

namespace HUDMind.UiTests;

/// <summary>
/// Full desktop End-to-End (E2E) UI Automation test suite for the real HUDMind.Native.exe WPF binary.
/// Uses built-in .NET 9 System.Windows.Automation + Win32 with zero external NuGet dependencies
/// and runs inside an isolated temporary workspace (--data-dir) so user config is never touched.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hudmind-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var exePath = UiAutomationDriver.LocateAppExecutable();
        var exeVerInfo = FileVersionInfo.GetVersionInfo(exePath);
        var expectedAppVersion = $"{exeVerInfo.ProductMajorPart}.{exeVerInfo.ProductMinorPart}.{exeVerInfo.ProductBuildPart}";
        Process? primaryProc = null;

        var results = new List<(string Name, bool Passed, string? Error)>();

        void RunStep(string name, Action body)
        {
            try
            {
                body();
                Console.WriteLine($"[PASS] {name}");
                results.Add((name, true, null));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] {name}: {ex.Message}");
                results.Add((name, false, ex.ToString()));
                throw;
            }
        }

        try
        {
            Console.WriteLine("=== HUDMind.UiTests (.NET 9 WPF UI Automation E2E Suite) ===");
            Console.WriteLine($"Target Binary: {exePath}");

            AutomationElement mainWin = null!;
            int pid = 0;

            RunStep("E1. Cold start -> 3-step First-Run Wizard -> Overlay Preview & Profile Rename", () =>
            {
                primaryProc = UiAutomationDriver.LaunchIsolatedApp(exePath, tempRoot);
                pid = primaryProc.Id;

                mainWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindMainWindow", timeoutMs: 20000);
                var wizardWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindWizardDialog", mainWin, timeoutMs: 15000);

                var stepTitle = UiAutomationDriver.WaitForElement(wizardWin, "LblWizardStepTitle");
                UiAutomationDriver.WaitForNonEmptyText(stepTitle, t => t.Contains("1"), "Wizard Step 1 header");

                var txtDisplay = UiAutomationDriver.WaitForElement(wizardWin, "TxtWizardDisplayName");
                UiAutomationDriver.SetText(txtDisplay, "E2eCaptain");

                var btnNext = UiAutomationDriver.WaitForElement(wizardWin, "BtnWizardNext");
                UiAutomationDriver.Invoke(btnNext);

                UiAutomationDriver.WaitForNonEmptyText(stepTitle, t => t.Contains("2"), "Wizard Step 2 header");
                UiAutomationDriver.Invoke(btnNext);

                UiAutomationDriver.WaitForNonEmptyText(stepTitle, t => t.Contains("3"), "Wizard Step 3 header");
                var btnTestHud = UiAutomationDriver.WaitForElement(wizardWin, "BtnWizardTestHud");
                UiAutomationDriver.Invoke(btnTestHud);

                var overlayWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindOverlayWindow", timeoutMs: 8000);
                var bounds = overlayWin.Current.BoundingRectangle;
                UiAutomationDriver.AssertTrue(
                    !bounds.IsEmpty && bounds.Width > 100 && bounds.Height > 25,
                    $"Expected visible OverlayWindow bounds, got {bounds}");

                UiAutomationDriver.Invoke(btnNext);
                UiAutomationDriver.WaitUntilWindowGone(pid, "HUDMindWizardDialog", mainWin);

                var activeUser = UiAutomationDriver.WaitForElement(mainWin, "TxtActiveUserName");
                UiAutomationDriver.WaitForNonEmptyText(
                    activeUser,
                    t => string.Equals(t.Trim(), "E2eCaptain", StringComparison.Ordinal),
                    "MainWindow active profile name after wizard");
            });

            RunStep("E2. Navigate across all 5 sidebar modules (Observer, AI Providers, Games, History, Settings)", () =>
            {
                UiAutomationDriver.SelectRadioOrTab(UiAutomationDriver.WaitForElement(mainWin, "NavAccounts"));
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnOpenRouterPkce");
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnAddAccount");

                UiAutomationDriver.SelectRadioOrTab(UiAutomationDriver.WaitForElement(mainWin, "NavGames"));
                _ = UiAutomationDriver.WaitForElement(mainWin, "CmbRulesGame");
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnSaveRules");

                UiAutomationDriver.SelectRadioOrTab(UiAutomationDriver.WaitForElement(mainWin, "NavHistory"));
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnRefreshHistory");
                _ = UiAutomationDriver.WaitForElement(mainWin, "TxtHistoryReviewViewer");

                UiAutomationDriver.SelectRadioOrTab(UiAutomationDriver.WaitForElement(mainWin, "NavSettings"));
                _ = UiAutomationDriver.WaitForElement(mainWin, "CmbLanguage");
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnWhatsNew");
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnDiagnostics");
                _ = UiAutomationDriver.WaitForElement(mainWin, "BtnSaveConfig");
            });

            RunStep("E3. Live ES <-> EN ResourceDictionary UI switch without restart & Settings save", () =>
            {
                var navCoach = UiAutomationDriver.WaitForElement(mainWin, "NavCoach");
                var navSettings = UiAutomationDriver.WaitForElement(mainWin, "NavSettings");
                var cmbLang = UiAutomationDriver.WaitForElement(mainWin, "CmbLanguage");

                UiAutomationDriver.SelectComboBoxItemContains(mainWin, pid, cmbLang, "English");

                UiAutomationDriver.WaitForNonEmptyText(navCoach, t => t.Contains("Observer"), "English NavCoach label");
                UiAutomationDriver.WaitForNonEmptyText(navSettings, t => t.Contains("Voice & Settings"), "English NavSettings label");
                var btnDiagEn = UiAutomationDriver.WaitForElement(mainWin, "BtnDiagnostics");
                UiAutomationDriver.WaitForNonEmptyText(btnDiagEn, t => t.Contains("Diagnostics"), "English Diagnostics button");
                var btnWhatsNewEn = UiAutomationDriver.WaitForElement(mainWin, "BtnWhatsNew");
                UiAutomationDriver.WaitForNonEmptyText(btnWhatsNewEn, t => t.Contains("What's New"), "English What's New button");

                UiAutomationDriver.SelectComboBoxItemContains(mainWin, pid, cmbLang, "Español");
                UiAutomationDriver.WaitForNonEmptyText(navCoach, t => t.Contains("Observador"), "Spanish NavCoach label");
                UiAutomationDriver.WaitForNonEmptyText(btnDiagEn, t => t.Contains("Diagnóstico"), "Spanish Diagnostics button");
                UiAutomationDriver.WaitForNonEmptyText(btnWhatsNewEn, t => t.Contains("Novedades"), "Spanish What's New button");

                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(mainWin, "BtnSaveConfig"));
            });

            RunStep("E4. Open What's New, Health Diagnostics & Profile Manager modals + create isolated profile", () =>
            {
                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(mainWin, "BtnWhatsNew"));
                var whatsNewWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindWhatsNewDialog", mainWin);
                UiAutomationDriver.AssertTrue(
                    whatsNewWin.Current.Name.Contains(expectedAppVersion, StringComparison.Ordinal),
                    $"Expected What's New dialog title to mention '{expectedAppVersion}', got '{whatsNewWin.Current.Name}'");
                var bodyEl = UiAutomationDriver.WaitForElement(whatsNewWin, "TxtWhatsNewBody");
                UiAutomationDriver.WaitForNonEmptyText(
                    bodyEl,
                    t => t.Contains(".hudmind", StringComparison.OrdinalIgnoreCase) && t.Contains("WinGet", StringComparison.OrdinalIgnoreCase),
                    $"What's New v{expectedAppVersion} notes");
                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(whatsNewWin, "BtnWhatsNewOk"));
                UiAutomationDriver.WaitUntilWindowGone(pid, "HUDMindWhatsNewDialog", mainWin);

                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(mainWin, "BtnDiagnostics"));
                var diagWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindDiagnosticsDialog", mainWin);
                _ = UiAutomationDriver.WaitForElement(diagWin, "BtnDiagnosticsReprobe", timeoutMs: 12000, requireEnabled: true);
                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(diagWin, "BtnDiagnosticsClose"));
                UiAutomationDriver.WaitUntilWindowGone(pid, "HUDMindDiagnosticsDialog", mainWin);

                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(mainWin, "BtnManageProfiles"));
                var mgrWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindProfileManagerDialog", mainWin);
                var uniqueIdBox = UiAutomationDriver.WaitForElement(mgrWin, "TxtProfileManagerUniqueId");
                var uniqueIdText = UiAutomationDriver.ReadText(uniqueIdBox);
                UiAutomationDriver.AssertTrue(
                    uniqueIdText.StartsWith("HM-", StringComparison.Ordinal) && uniqueIdText.Length >= 15,
                    $"Expected unique HM-XXXX-XXXX-XXXX identifier, got '{uniqueIdText}'");

                var profileNameInput = UiAutomationDriver.WaitForElement(mgrWin, "TxtProfileManagerName");
                UiAutomationDriver.SetText(profileNameInput, "SquadBeta");
                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(mgrWin, "BtnProfileManagerCreate"));
                UiAutomationDriver.WaitUntilWindowGone(pid, "HUDMindProfileManagerDialog", mainWin);

                var activeUser = UiAutomationDriver.WaitForElement(mainWin, "TxtActiveUserName");
                UiAutomationDriver.WaitForNonEmptyText(
                    activeUser,
                    t => string.Equals(t.Trim(), "SquadBeta", StringComparison.Ordinal),
                    "Switched active user display name");
            });

            RunStep("E5. Toggle text-only communication & verify 0% GPU OverlayWindow from Settings preview", () =>
            {
                var cmbComm = UiAutomationDriver.WaitForElement(mainWin, "CmbCommModeSettings");
                UiAutomationDriver.SelectComboBoxItemContains(mainWin, pid, cmbComm, "Solo texto");
                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(mainWin, "BtnPreviewOverlay"));

                var overlayWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindOverlayWindow", timeoutMs: 8000);
                var bounds = overlayWin.Current.BoundingRectangle;
                UiAutomationDriver.AssertTrue(
                    !bounds.IsEmpty && bounds.Width > 100 && bounds.Height > 25,
                    $"Expected visible Settings-triggered OverlayWindow, got {bounds}");
            });

            RunStep("E6. Two-process single-instance Named Pipe IMPORT:<.hudmind> -> AES-256-GCM decrypt dialog", () =>
            {
                var fixtureWorkspace = Path.Combine(tempRoot, "fixture-export-source");
                Directory.CreateDirectory(Path.Combine(fixtureWorkspace, "config"));
                Directory.CreateDirectory(Path.Combine(fixtureWorkspace, "profiles"));
                var fixtureRepo = new ConfigRepository(fixtureWorkspace, secretProtector: new Win32DpapiSecretProtector());
                fixtureRepo.EnsureDefaultProfilesSeeded();
                var restoredConfig = new UserConfig
                {
                    HasCompletedOnboarding = true,
                    LastSeenAppVersion = expectedAppVersion,
                    TextLanguage = "es",
                    VoiceLanguage = "es",
                    CheckUpdatesOnStartup = false,
                    CommunicationMode = "text_only",
                    EnableOverlay = true
                };
                fixtureRepo.SaveUserConfig(restoredConfig);

                const string archivePass = "E2ePass#2026";
                var bundleBytes = ProfileArchiveCodec.ExportBundle(
                    fixtureRepo,
                    archivePass,
                    includeApiAccounts: false,
                    profileMetadata: new UserProfileRecord
                    {
                        DisplayName = "RestoredChampion",
                        AvatarBadge = "🏆",
                        AccentHex = "#F59E0B",
                        FavoriteGameId = "rocket_league"
                    });
                var archivePath = Path.Combine(tempRoot, "champion-backup.hudmind");
                File.WriteAllBytes(archivePath, bundleBytes);

                using var secondProc = UiAutomationDriver.LaunchIsolatedApp(exePath, tempRoot, archivePath);
                UiAutomationDriver.AssertTrue(
                    secondProc.WaitForExit(12000) && secondProc.ExitCode == 0,
                    $"Second single-instance launcher did not exit cleanly (HasExited={secondProc.HasExited})");

                var pwdWin = UiAutomationDriver.WaitForWindow(pid, "HUDMindPassphraseDialog", mainWin, timeoutMs: 12000);
                var pwdBox = UiAutomationDriver.WaitForElement(pwdWin, "PwdArchivePassphrase");
                UiAutomationDriver.TypeUnicodeIntoPasswordControl(pwdWin, pwdBox, pid, archivePass);
                UiAutomationDriver.Invoke(UiAutomationDriver.WaitForElement(pwdWin, "BtnArchivePassphraseOk"));
                UiAutomationDriver.WaitUntilWindowGone(pid, "HUDMindPassphraseDialog", mainWin, timeoutMs: 12000);

                var activeUser = UiAutomationDriver.WaitForElement(mainWin, "TxtActiveUserName");
                UiAutomationDriver.WaitForNonEmptyText(
                    activeUser,
                    t => string.Equals(t.Trim(), "RestoredChampion", StringComparison.Ordinal),
                    "Restored backup active profile display name");
            });

            RunStep("E7. Graceful WindowPattern.Close() & on-disk persistence without crash logs", () =>
            {
                UiAutomationDriver.CloseWindow(mainWin);
                UiAutomationDriver.AssertTrue(
                    primaryProc!.WaitForExit(15000),
                    "HUDMind.Native.exe did not close within 15s after WindowPattern.Close()");
                UiAutomationDriver.AssertTrue(
                    primaryProc.ExitCode == 0,
                    $"Expected exit code 0 on graceful close, got {primaryProc.ExitCode}");

                var userStore = new UserProfileStore(tempRoot);
                var idx = userStore.LoadOrInitialize();
                UiAutomationDriver.AssertTrue(
                    idx.Profiles.Count == 2,
                    $"Expected 2 isolated user profiles (E2eCaptain + RestoredChampion), found {idx.Profiles.Count}");

                var active = idx.Profiles.First(p => p.ProfileId == idx.ActiveProfileId);
                UiAutomationDriver.AssertTrue(
                    string.Equals(active.DisplayName, "RestoredChampion", StringComparison.Ordinal),
                    $"Expected persisted active profile 'RestoredChampion', got '{active.DisplayName}'");

                var repo = new ConfigRepository(
                    userStore.ResolveWorkspaceRoot(active.ProfileId),
                    secretProtector: new Win32DpapiSecretProtector());
                var cfg = repo.LoadUserConfig();
                UiAutomationDriver.AssertTrue(cfg.HasCompletedOnboarding, "Expected HasCompletedOnboarding=true persisted.");
                UiAutomationDriver.AssertTrue(
                    string.Equals(cfg.LastSeenAppVersion, expectedAppVersion, StringComparison.Ordinal),
                    $"Expected LastSeenAppVersion='{expectedAppVersion}', got '{cfg.LastSeenAppVersion}'");
                UiAutomationDriver.AssertTrue(
                    cfg.WindowWidth is >= 920,
                    $"Expected persisted WindowWidth >= 920, got {cfg.WindowWidth}");

                var seededProfiles = Directory.GetDirectories(repo.ProfilesDir);
                UiAutomationDriver.AssertTrue(
                    seededProfiles.Length >= 7,
                    $"Expected >= 7 seeded game profiles from vault, found {seededProfiles.Length}");

                var logsDir = Path.Combine(tempRoot, "logs");
                if (Directory.Exists(logsDir))
                {
                    var crashLogs = Directory.GetFiles(logsDir, "crash-*.log");
                    UiAutomationDriver.AssertTrue(
                        crashLogs.Length == 0,
                        $"Unexpected crash logs created: {string.Join(", ", crashLogs)}");
                }
            });

            Console.WriteLine();
            Console.WriteLine($"UI E2E SUITE RESULT: {results.Count(r => r.Passed)} passed, {results.Count(r => !r.Passed)} failed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL UI E2E FAILURE: {ex}");
            Console.WriteLine($"UI E2E SUITE RESULT: {results.Count(r => r.Passed)} passed, {results.Count(r => !r.Passed) + (results.Any(r => !r.Passed) ? 0 : 1)} failed.");
            return 1;
        }
        finally
        {
            try
            {
                if (primaryProc is not null && !primaryProc.HasExited)
                    primaryProc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore cleanup kill races
            }
            primaryProc?.Dispose();

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup
            }
        }
    }
}
