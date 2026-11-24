using System.Text.Json;
using SnipStudio.WinUI;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckLegacySettingsImport(string output)
    {
        DesktopPreferences current = new()
        {
            Theme = "Light",
            HideTaskbars = false,
            ResizeToSnip = false,
            StrokeWidth = 5,
            RecordingFolder = @"C:\SnipImport\Existing Recordings",
            DefaultSystemAudioDeviceId = "previous-choice",
            HotkeysEnabled = true,
            UsePrintScreen = true,
            KeepShortcutsAfterClose = true,
            StartupWithWindows = false,
            KeepRunningInTray = false,
            EditorApplicationPath = @"C:\SnipImport\ExistingEditor.exe",
            LastUpdateCheckUtc = "2026-11-20T12:00:00.0000000Z",
            AdditionalSettings = new() { ["FutureFeature"] = JsonSerializer.SerializeToElement(new { enabled = true }) }
        };

        const string fixture = """
            <?xml version="1.0" encoding="utf-8"?>
            <Preferences xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Theme>dark</Theme>
              <AnnotationToolbarPosition>left</AnnotationToolbarPosition>
              <ShowTooltips>false</ShowTooltips>
              <StartCompact>false</StartCompact>
              <RequireCtrlForWheelZoom>false</RequireCtrlForWheelZoom>
              <AutoCheckForUpdates>false</AutoCheckForUpdates>
              <LastUpdateCheckUtc>2026-01-01T12:00:00.0000000Z</LastUpdateCheckUtc>
              <RecentInkColors><int>-1</int><int>-1</int><int>-65536</int></RecentInkColors>
              <InkArgb>-16776961</InkArgb>
              <StrokeWidth>invalid</StrokeWidth>
              <DelaySeconds>5</DelaySeconds>
              <CaptureModeIndex>5</CaptureModeIndex>
              <SaveFolder>C:\SnipImport\Last Output</SaveFolder>
              <RememberCaptureDelay>false</RememberCaptureDelay>
              <MultipleWindows>true</MultipleWindows>
              <AutoCopyScreenshotChanges>false</AutoCopyScreenshotChanges>
              <AutoSaveOriginalScreenshots>true</AutoSaveOriginalScreenshots>
              <AskSaveEditedScreenshots>false</AskSaveEditedScreenshots>
              <ScreenshotFolder>C:\SnipImport\Previous Screenshots</ScreenshotFolder>
              <AddScreenshotBorder>true</AddScreenshotBorder>
              <ScreenshotBorderArgb>-65536</ScreenshotBorderArgb>
              <ScreenshotBorderWidth>7</ScreenshotBorderWidth>
              <HdrColorCorrection>true</HdrColorCorrection>
              <HideTaskbarsDuringCapture>false</HideTaskbarsDuringCapture>
              <TaskbarVisibilityConfigured>true</TaskbarVisibilityConfigured>
              <KeepPrimaryTaskbarVisible>false</KeepPrimaryTaskbarVisible>
              <AutoCopyRecordingChanges>false</AutoCopyRecordingChanges>
              <AutoSaveOriginalRecordings>false</AutoSaveOriginalRecordings>
              <AskSaveEditedRecordings>false</AskSaveEditedRecordings>
              <RecordingFolder>relative\Recordings</RecordingFolder>
              <DefaultMicrophone>false</DefaultMicrophone>
              <DefaultSystemAudio>false</DefaultSystemAudio>
              <DefaultMicrophoneDeviceId>device-00-Aa</DefaultMicrophoneDeviceId>
              <DefaultSystemAudioDeviceId />
              <UsePrintScreen>false</UsePrintScreen>
              <KeepShortcutsAfterClose>false</KeepShortcutsAfterClose>
              <StartupWithWindows>true</StartupWithWindows>
              <KeepRunningInTray>true</KeepRunningInTray>
              <EditorApplicationPath>C:\Example\Editor.exe</EditorApplicationPath>
              <TranslationServerUrl>http://localhost/example</TranslationServerUrl>
              <TranslationProvider>LibreTranslate</TranslationProvider>
              <TranslationSourceLanguage>pt</TranslationSourceLanguage>
              <TranslationTargetLanguage>es</TranslationTargetLanguage>
              <FutureFeature>false</FutureFeature>
              <UnknownExtension>No import</UnknownExtension>
            </Preferences>
            """;
        string source = Path.Combine(output, "legacy-preferences-synthetic.xml");
        File.WriteAllText(source, fixture);
        byte[] originalBytes = File.ReadAllBytes(source);
        DesktopPreferences draft = LegacySettingsImport.Import(source, current);

        Check(!ReferenceEquals(draft, current) && current.Theme == "Light"
            && current.DefaultSystemAudioDeviceId == "previous-choice", "Legacy import produces a detached draft");
        Check(draft.Theme == "Dark" && draft.AnnotationToolbarPosition == "Left"
            && !draft.ShowTooltips && !draft.StartCompact && !draft.ResizeToSnip && !draft.RequireCtrlForWheelZoom,
            "Legacy workspace preferences import while omitted values survive");
        Check(draft.InkArgb == 0xFF0000FF && draft.StrokeWidth == 5, "Legacy signed ARGB imports without dropping invalid-field fallback");
        Check(draft.RecentInkColors.SequenceEqual(new uint[] { 0xFFFFFFFF, 0xFFFF0000 }),
            "Legacy recent color order and exact unsigned ARGB are retained");
        Check(draft.CaptureDelaySeconds == 5 && !draft.RememberCaptureDelay && draft.CaptureInNewWindow
            && !draft.AutoCopyCaptures && draft.AutoSaveOriginalCaptures && !draft.AskSaveEditedScreenshots
            && draft.LastScreenshotCaptureKind == CaptureKind.AllMonitors && draft.LastSaveFolder == @"C:\SnipImport\Last Output",
            "Legacy screenshot workflow preferences import");
        Check(draft.ScreenshotFolder == @"C:\SnipImport\Previous Screenshots" && draft.RecordingFolder == current.RecordingFolder,
            "A valid folder imports exactly and a relative folder keeps the current value");
        Check(draft.AddScreenshotBorder && draft.ScreenshotBorderArgb == 0xFFFF0000 && draft.ScreenshotBorderWidth == 7
            && draft.UseHdrColorCorrection && !draft.HideTaskbars && !draft.KeepPrimaryTaskbarVisible,
            "Legacy border, color correction and explicit taskbar choices import");
        Check(!draft.AutoCopyRecordingChanges && !draft.AutoSaveOriginalRecordings && !draft.AskSaveEditedRecordings
            && !draft.DefaultMicrophone && !draft.DefaultSystemAudio
            && draft.DefaultMicrophoneDeviceId == "device-00-Aa" && draft.DefaultSystemAudioDeviceId == "",
            "Legacy recording preferences preserve the exact device ID and an explicit default-device selection");
        Check(!draft.AutoCheckForUpdates && draft.LastUpdateCheckUtc == current.LastUpdateCheckUtc,
            "Import applies the update choice and keeps WinUI update history");
        Check(draft.HotkeysEnabled && !draft.UsePrintScreen && !draft.KeepShortcutsAfterClose
            && draft.StartupWithWindows && draft.KeepRunningInTray && draft.EditorApplicationPath == @"C:\Example\Editor.exe",
            "Import restores legacy Windows and external editor choices to a reviewable draft");
        Check(draft.TranslationSourceLanguage == "pt" && draft.TranslationTargetLanguage == "es"
            && !draft.UseConfiguredTranslationServer && draft.TranslationServerAddress == "",
            "Import remembers languages while keeping remote translation an explicit current choice");
        Check(draft.AdditionalSettings is { Count: 1 } additionalSettings
            && !ReferenceEquals(additionalSettings, current.AdditionalSettings)
            && additionalSettings["FutureFeature"].GetProperty("enabled").GetBoolean(),
            "Import preserves current extension data and ignores unknown legacy settings");
        Check(File.ReadAllBytes(source).SequenceEqual(originalBytes), "Legacy preferences file remains byte-for-byte intact");

        string invalidSource = Path.Combine(output, "legacy-preferences-rejection.xml");
        File.WriteAllText(invalidSource, "<!DOCTYPE Preferences [<!ENTITY name 'Dark'>]><Preferences><Theme>&name;</Theme></Preferences>");
        Expect<InvalidDataException>(() => LegacySettingsImport.Import(invalidSource, current), "Legacy import rejects document type declarations");
        File.WriteAllText(invalidSource, "<Preferences><Theme>Dark</Theme><Theme>Light</Theme></Preferences>");
        Expect<InvalidDataException>(() => LegacySettingsImport.Import(invalidSource, current), "Legacy import rejects duplicate compatible fields");
        File.WriteAllText(invalidSource, "<Other><Theme>Dark</Theme></Other>");
        Expect<InvalidDataException>(() => LegacySettingsImport.Import(invalidSource, current), "Legacy import rejects other XML formats");
        File.WriteAllText(invalidSource, "<Preferences><Unknown>value</Unknown></Preferences>");
        Expect<InvalidDataException>(() => LegacySettingsImport.Import(invalidSource, current), "Legacy import identifies files with no compatible fields");
        using (FileStream oversized = File.Create(invalidSource))
            oversized.SetLength(1024 * 1024 + 1);
        Expect<InvalidDataException>(() => LegacySettingsImport.Import(invalidSource, current), "Legacy import rejects oversized files");

        File.WriteAllText(invalidSource, "<Preferences><HideTaskbarsDuringCapture>false</HideTaskbarsDuringCapture></Preferences>");
        Check(LegacySettingsImport.Import(invalidSource, current).HideTaskbars && !current.HideTaskbars,
            "Legacy preconfiguration taskbar behavior is retained without modifying current preferences");
        completedGroups.Add("Read-only legacy XML import, typed preferences, current OS choices and input rejection");
        CheckFirstLaunchSettingsMigration(output, fixture);
    }

    private static void CheckFirstLaunchSettingsMigration(string output, string fixture)
    {
        string folder = Directory.CreateDirectory(Path.Combine(output, "stable-settings-first-launch")).FullName;
        string path = Path.Combine(folder, "preferences.json");
        string legacy = Path.Combine(folder, "preferences.xml");
        DesktopPreferences defaults = new DesktopPreferencesStore(path, legacy).Load();
        Check(!File.Exists(path) && Path.GetFileName(defaults.ScreenshotFolder) == "Screenshots" &&
            Path.GetFileName(defaults.RecordingFolder) == "Screen Recordings",
            "Fresh stable preferences use the original media folders without writing a profile");

        string original = fixture.Replace("<UsePrintScreen>false", "<UsePrintScreen>true", StringComparison.Ordinal);
        File.WriteAllText(legacy, original);
        byte[] legacyBytes = File.ReadAllBytes(legacy);
        var first = new DesktopPreferencesStore(path, legacy);
        var parallel = new DesktopPreferencesStore(path, legacy);
        DesktopPreferences[] results = Task.WhenAll(Task.Run(first.Load), Task.Run(parallel.Load)).GetAwaiter().GetResult();
        Check(results.All(value => value.Theme == "Dark" && value.CaptureDelaySeconds == 5 && value.CaptureInNewWindow &&
            value.ScreenshotFolder == @"C:\SnipImport\Previous Screenshots" && !value.DefaultMicrophone &&
            value.DefaultMicrophoneDeviceId == "device-00-Aa" && !value.AutoCheckForUpdates),
            "Concurrent first loads receive the same migrated capture, folder, audio and update choices");
        Check(results.All(value => value.StartupWithWindows && !value.UsePrintScreen && !value.HotkeysEnabled &&
            !value.KeepRunningInTray && !value.UseConfiguredTranslationServer),
            "Automatic import keeps the prior startup preference and requires review for unconfirmed Windows and remote actions");
        Check(File.Exists(path) && !File.Exists(path + ".bak") && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes) &&
            (first.LoadWarning?.Contains("carried forward", StringComparison.Ordinal) == true ||
                parallel.LoadWarning?.Contains("carried forward", StringComparison.Ordinal) == true),
            "First launch publishes one new profile and retains the original XML bytes");

        File.WriteAllText(legacy, original.Replace("<Theme>dark", "<Theme>Light", StringComparison.Ordinal));
        byte[] migratedBytes = File.ReadAllBytes(path);
        var reopened = new DesktopPreferencesStore(path, legacy);
        Check(reopened.Load().Theme == "Dark" && reopened.LoadWarning is null &&
            File.ReadAllBytes(path).SequenceEqual(migratedBytes), "An existing stable profile prevents repeated XML import");

        string backupPath = Path.Combine(folder, "backup-only.json");
        File.Copy(path, backupPath + ".bak", overwrite: false);
        var backupOnly = new DesktopPreferencesStore(backupPath, legacy);
        Check(backupOnly.Load().Theme == "Dark" && !File.Exists(backupPath) &&
            backupOnly.LoadWarning?.Contains("backup", StringComparison.Ordinal) == true &&
            File.ReadAllBytes(backupPath + ".bak").SequenceEqual(migratedBytes),
            "A stable JSON backup takes precedence over legacy XML without writing a primary");

        string corruptPath = Path.Combine(folder, "corrupt.json");
        File.WriteAllText(corruptPath, "{broken}");
        var corrupt = new DesktopPreferencesStore(corruptPath, legacy);
        Check(corrupt.Load().Theme == "System" && corrupt.LoadWarning is not null &&
            File.ReadAllText(corruptPath) == "{broken}", "An unreadable stable JSON primary is kept rather than migrated over");

        string rejectedPath = Path.Combine(folder, "rejected.json");
        const string invalid = "<Preferences><Theme>Dark</Theme><Theme>Light</Theme></Preferences>";
        File.WriteAllText(legacy, invalid);
        var rejected = new DesktopPreferencesStore(rejectedPath, legacy);
        Check(rejected.Load().Theme == "System" && !File.Exists(rejectedPath) &&
            rejected.LoadWarning?.Contains("could not be carried forward", StringComparison.Ordinal) == true &&
            File.ReadAllText(legacy) == invalid, "A failed automatic import reports recovery while keeping previous data intact");

        File.WriteAllText(legacy, original.Replace("</Preferences>",
            "<TrayBehaviorConfirmed>true</TrayBehaviorConfirmed></Preferences>", StringComparison.Ordinal));
        using (FileStream held = new(legacy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var locked = new DesktopPreferencesStore(rejectedPath, legacy);
            Check(locked.Load().Theme == "System" && !File.Exists(rejectedPath) &&
                locked.LoadWarning?.Contains("could not be carried forward", StringComparison.Ordinal) == true,
                "A sharing violation keeps the first-launch destination empty and reports the deferred import");
        }
        DesktopPreferences retried = new DesktopPreferencesStore(rejectedPath, legacy).Load();
        Check(File.Exists(rejectedPath) && retried.Theme == "Dark" && retried.KeepRunningInTray &&
            !retried.UsePrintScreen, "Reopening retries first launch and honors confirmed legacy tray behavior");
        completedGroups.Add("Stable profile first launch, concurrent import, legacy byte preservation and recovery");
    }
}
