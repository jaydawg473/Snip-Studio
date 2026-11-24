using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

internal static class LegacySettingsImport
{
    private const int MaximumBytes = 1024 * 1024;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnipStudio", "preferences.xml");

    internal static DesktopPreferences Import(string sourcePath, DesktopPreferences current, bool firstLaunch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(current);
        if (!Path.IsPathFullyQualified(sourcePath))
            throw new ArgumentException("Choose a settings file with an absolute path.", nameof(sourcePath));

        XElement root = ReadSettings(sourcePath);
        ILookup<XName, XElement> settings = root.Elements().ToLookup(element => element.Name);
        DesktopPreferences draft = current.Clone();
        int imported = 0;

        draft.Theme = ReadChoice("Theme", "System", "Light", "Dark") ?? draft.Theme;
        draft.AnnotationToolbarPosition = ReadChoice("AnnotationToolbarPosition", "Top", "Right", "Bottom", "Left") ?? draft.AnnotationToolbarPosition;
        draft.ShowTooltips = ReadBoolean("ShowTooltips") ?? draft.ShowTooltips;
        draft.StartCompact = ReadBoolean("StartCompact") ?? draft.StartCompact;
        draft.ResizeToSnip = ReadBoolean("ResizeToSnip") ?? draft.ResizeToSnip;
        draft.RequireCtrlForWheelZoom = ReadBoolean("RequireCtrlForWheelZoom") ?? draft.RequireCtrlForWheelZoom;
        draft.AutoCheckForUpdates = ReadBoolean("AutoCheckForUpdates") ?? draft.AutoCheckForUpdates;
        draft.InkArgb = ReadColor("InkArgb") ?? draft.InkArgb;
        draft.RecentInkColors = ReadRecentInkColors() ?? draft.RecentInkColors;
        draft.StrokeWidth = ReadInteger("StrokeWidth", 1, 100) ?? draft.StrokeWidth;
        draft.LastSaveFolder = ReadFolder("SaveFolder") ?? draft.LastSaveFolder;
        draft.EditorApplicationPath = ReadExecutable("EditorApplicationPath") ?? draft.EditorApplicationPath;
        // The original stored its default Print Screen choice without recording
        // consent. Keep automatic import passive; the reviewed draft can set it.
        if (!firstLaunch) draft.UsePrintScreen = ReadBoolean("UsePrintScreen") ?? draft.UsePrintScreen;
        draft.KeepShortcutsAfterClose = ReadBoolean("KeepShortcutsAfterClose") ?? draft.KeepShortcutsAfterClose;
        if (!firstLaunch || (ReadScalar("TrayBehaviorConfirmed")?.Trim() is "true" or "1"))
            draft.KeepRunningInTray = ReadBoolean("KeepRunningInTray") ?? draft.KeepRunningInTray;
        draft.StartupWithWindows = ReadBoolean("StartupWithWindows") ?? draft.StartupWithWindows;
        draft.TranslationSourceLanguage = ReadLanguage("TranslationSourceLanguage") ?? draft.TranslationSourceLanguage;
        draft.TranslationTargetLanguage = ReadLanguage("TranslationTargetLanguage") ?? draft.TranslationTargetLanguage;

        draft.CaptureDelaySeconds = ReadInteger("DelaySeconds", 0, 10) ?? draft.CaptureDelaySeconds;
        if (ReadCaptureKind() is CaptureKind captureKind) draft.LastScreenshotCaptureKind = captureKind;
        draft.RememberCaptureDelay = ReadBoolean("RememberCaptureDelay") ?? draft.RememberCaptureDelay;
        draft.CaptureInNewWindow = ReadBoolean("MultipleWindows") ?? draft.CaptureInNewWindow;
        draft.AutoCopyCaptures = ReadBoolean("AutoCopyScreenshotChanges") ?? draft.AutoCopyCaptures;
        draft.AutoSaveOriginalCaptures = ReadBoolean("AutoSaveOriginalScreenshots") ?? draft.AutoSaveOriginalCaptures;
        draft.AskSaveEditedScreenshots = ReadBoolean("AskSaveEditedScreenshots") ?? draft.AskSaveEditedScreenshots;
        draft.ScreenshotFolder = ReadFolder("ScreenshotFolder") ?? draft.ScreenshotFolder;
        draft.AddScreenshotBorder = ReadBoolean("AddScreenshotBorder") ?? draft.AddScreenshotBorder;
        draft.ScreenshotBorderArgb = ReadColor("ScreenshotBorderArgb") ?? draft.ScreenshotBorderArgb;
        draft.ScreenshotBorderWidth = ReadInteger("ScreenshotBorderWidth", 1, 20) ?? draft.ScreenshotBorderWidth;
        draft.UseHdrColorCorrection = ReadBoolean("HdrColorCorrection") ?? draft.UseHdrColorCorrection;
        if (ReadBoolean("HideTaskbarsDuringCapture") is bool hideTaskbars)
        {
            // The stable app treats an unconfigured value as the current default.
            draft.HideTaskbars = ReadBoolean("TaskbarVisibilityConfigured") != true || hideTaskbars;
        }
        draft.KeepPrimaryTaskbarVisible = ReadBoolean("KeepPrimaryTaskbarVisible") ?? draft.KeepPrimaryTaskbarVisible;

        draft.AutoCopyRecordingChanges = ReadBoolean("AutoCopyRecordingChanges") ?? draft.AutoCopyRecordingChanges;
        draft.AutoSaveOriginalRecordings = ReadBoolean("AutoSaveOriginalRecordings") ?? draft.AutoSaveOriginalRecordings;
        draft.AskSaveEditedRecordings = ReadBoolean("AskSaveEditedRecordings") ?? draft.AskSaveEditedRecordings;
        draft.RecordingFolder = ReadFolder("RecordingFolder") ?? draft.RecordingFolder;
        draft.DefaultMicrophone = ReadBoolean("DefaultMicrophone") ?? draft.DefaultMicrophone;
        draft.DefaultSystemAudio = ReadBoolean("DefaultSystemAudio") ?? draft.DefaultSystemAudio;
        draft.DefaultMicrophoneDeviceId = ReadDeviceId("DefaultMicrophoneDeviceId") ?? draft.DefaultMicrophoneDeviceId;
        draft.DefaultSystemAudioDeviceId = ReadDeviceId("DefaultSystemAudioDeviceId") ?? draft.DefaultSystemAudioDeviceId;

        if (imported == 0)
            throw new InvalidDataException("No compatible Snip Studio settings were found in this file.");

        return draft;

        XElement? ReadElement(string name)
        {
            using IEnumerator<XElement> values = settings[name].GetEnumerator();
            if (!values.MoveNext()) return null;
            XElement element = values.Current;
            if (values.MoveNext())
                throw new InvalidDataException("The settings file contains a duplicate preference.");
            return element;
        }

        string? ReadScalar(string name) => ReadElement(name) is { HasElements: false } element ? element.Value : null;

        CaptureKind? ReadCaptureKind()
        {
            // Legacy index 4 invokes scrolling capture and is not a screenshot shape.
            CaptureKind? kind = ReadScalar("CaptureModeIndex")?.Trim() switch
            {
                "0" => CaptureKind.Rectangle, "1" => CaptureKind.Freeform, "2" => CaptureKind.Window,
                "3" => CaptureKind.Monitor, "5" => CaptureKind.AllMonitors, _ => null
            };
            if (kind.HasValue) imported++;
            return kind;
        }

        uint[]? ReadRecentInkColors()
        {
            if (ReadElement("RecentInkColors") is not { } colors) return null;
            var values = new List<uint>();
            foreach (XElement item in colors.Elements())
            {
                if (item.Name != "int" || item.HasElements ||
                    !int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int argb)) return null;
                uint color = unchecked((uint)argb);
                if (!values.Contains(color)) values.Add(color);
                if (values.Count == 8) break;
            }
            if (!colors.HasElements && !string.IsNullOrWhiteSpace(colors.Value)) return null;
            imported++;
            return values.ToArray();
        }

        string? ReadExecutable(string name)
        {
            string? candidate = ReadScalar(name)?.Trim();
            if (candidate is null) return null;
            if (candidate.Length > 0 && (candidate.Length > 32767 || !Path.IsPathFullyQualified(candidate) ||
                candidate.Any(char.IsControl) || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                !Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase))) return null;
            imported++;
            return candidate;
        }

        string? ReadLanguage(string name)
        {
            string? code = ReadScalar(name)?.Trim();
            if (code is null || code.Length > 35 || !code.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                return null;
            imported++;
            return code;
        }

        bool? ReadBoolean(string name)
        {
            bool? value = ReadScalar(name)?.Trim() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => null
            };
            if (value.HasValue) imported++;
            return value;
        }

        int? ReadInteger(string name, int minimum, int maximum)
        {
            if (!int.TryParse(ReadScalar(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum)
                return null;

            imported++;
            return value;
        }

        uint? ReadColor(string name)
        {
            // WinForms serializes Color.ToArgb() as a signed 32-bit integer.
            if (!int.TryParse(ReadScalar(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int argb))
                return null;

            imported++;
            return unchecked((uint)argb);
        }

        string? ReadChoice(string name, params string[] choices)
        {
            string? raw = ReadScalar(name);
            string? choice = choices.FirstOrDefault(value => string.Equals(value, raw, StringComparison.OrdinalIgnoreCase));
            if (choice is not null) imported++;
            return choice;
        }

        string? ReadFolder(string name)
        {
            string? folder = ReadScalar(name);
            if (string.IsNullOrWhiteSpace(folder) || folder.Length > 32767
                || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder))
                return null;

            imported++;
            return folder;
        }

        string? ReadDeviceId(string name)
        {
            string? deviceId = ReadScalar(name);
            if (deviceId is null || deviceId.Length > 2048 || deviceId.Any(char.IsControl))
                return null;

            imported++;
            return deviceId;
        }
    }

    private static XElement ReadSettings(string sourcePath)
    {
        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length == 0 || source.Length > MaximumBytes)
            throw new InvalidDataException("The settings file is empty or exceeds 1 MB.");

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            IgnoreComments = true
        };

        try
        {
            using XmlReader reader = XmlReader.Create(source, settings);
            XElement? root = XDocument.Load(reader).Root;
            if (root is null || root.Name != "Preferences")
                throw new InvalidDataException("The file is not a legacy Snip Studio settings file.");
            return root;
        }
        catch (XmlException)
        {
            throw new InvalidDataException("The legacy settings file is not valid XML.");
        }
    }
}
