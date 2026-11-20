using System.Text.Json;
using SnipStudio.WinUI;

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
              <RecentInkColors><int>-1</int></RecentInkColors>
              <InkArgb>-16776961</InkArgb>
              <StrokeWidth>invalid</StrokeWidth>
              <DelaySeconds>5</DelaySeconds>
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
        Check(draft.CaptureDelaySeconds == 5 && !draft.RememberCaptureDelay && draft.CaptureInNewWindow
            && !draft.AutoCopyCaptures && draft.AutoSaveOriginalCaptures && !draft.AskSaveEditedScreenshots,
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
        Check(draft.HotkeysEnabled && draft.UsePrintScreen && draft.KeepShortcutsAfterClose
            && !draft.StartupWithWindows && !draft.KeepRunningInTray && draft.EditorApplicationPath == current.EditorApplicationPath,
            "Import keeps WinUI operating system and external editor choices");
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
    }
}
