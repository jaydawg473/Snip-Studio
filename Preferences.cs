using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;

namespace SnipStudio
{
    public sealed class Preferences
    {
        public int CaptureModeIndex = 0;
        public int DelaySeconds = 0;
        public bool RememberCaptureDelay = true;
        public int InkArgb = Color.FromArgb(220, 38, 38).ToArgb();
        public int[] RecentInkColors = new int[0];
        public int StrokeWidth = 4;
        public string SaveFolder = "";

        public bool AutoCopyScreenshotChanges = true;
        public bool AutoSaveOriginalScreenshots = true;
        public string ScreenshotFolder = DefaultScreenshotFolder();
        public bool AskSaveEditedScreenshots = true;
        public bool MultipleWindows = true;
        public bool AddScreenshotBorder = false;
        public int ScreenshotBorderArgb = Color.FromArgb(77, 163, 255).ToArgb();
        public int ScreenshotBorderWidth = 2;
        public bool HdrColorCorrection = false;
        public bool HideTaskbarsDuringCapture = true;
        public bool TaskbarVisibilityConfigured = false;
        public bool KeepPrimaryTaskbarVisible = true;

        public bool AutoCopyRecordingChanges = true;
        public bool AutoSaveOriginalRecordings = true;
        public string RecordingFolder = DefaultRecordingFolder();
        public bool AskSaveEditedRecordings = true;
        public bool DefaultMicrophone = true;
        public bool DefaultSystemAudio = true;
        public string DefaultMicrophoneDeviceId = "";
        public string DefaultSystemAudioDeviceId = "";

        public string Theme = "Dark";
        public string AnnotationToolbarPosition = "Top";
        public bool ShowTooltips = true;
        public bool AutoCheckForUpdates = true;
        public string LastUpdateCheckUtc = "";
        public bool RequireCtrlForWheelZoom = true;
        public string TranslationServerUrl = "http://127.0.0.1:5000/";
        public string TranslationTargetLanguage = "en";
        public string TranslationProvider = "Offline";
        public string TranslationSourceLanguage = "";
        public bool StartCompact = true;
        public bool ResizeToSnip = true;
        public bool UsePrintScreen = true;
        public bool KeepShortcutsAfterClose = true;
        public bool StartupWithWindows = false;
        public bool KeepRunningInTray = false;
        public bool TrayBehaviorConfirmed = false;
        public string EditorApplicationPath = DefaultEditorPath();
        public string EditorApplicationName = "Paint";

        // Retain settings written by another compatible version rather than silently
        // dropping them when this version changes one of its own preferences.
        [XmlAnyElement] public XmlElement[] AdditionalSettings;
        private static readonly object fileGate = new object();
        private static DateTime latestUpdateCheckUtc = DateTime.MinValue;

        private static string DefaultScreenshotFolder()
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (String.IsNullOrEmpty(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "Screenshots");
        }

        private static string DefaultRecordingFolder()
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (String.IsNullOrEmpty(videos)) videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            return Path.Combine(videos, "Screen Recordings");
        }

        private static string DefaultEditorPath()
        {
            string paint = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe");
            return File.Exists(paint) ? paint : "";
        }

        public Preferences Clone()
        {
            Preferences copy = (Preferences)MemberwiseClone();
            if (RecentInkColors != null) copy.RecentInkColors = (int[])RecentInkColors.Clone();
            if (AdditionalSettings != null)
            {
                copy.AdditionalSettings = new XmlElement[AdditionalSettings.Length];
                for (int i = 0; i < AdditionalSettings.Length; i++)
                    copy.AdditionalSettings[i] = AdditionalSettings[i] == null ? null : (XmlElement)AdditionalSettings[i].CloneNode(true);
            }
            return copy;
        }

        public void CopyFrom(Preferences source)
        {
            if (source == null) throw new ArgumentNullException("source");
            Preferences copy = source.Clone();
            foreach (FieldInfo field in typeof(Preferences).GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(this, field.GetValue(copy));
        }

        private void Normalize()
        {
            // Earlier builds saved the old default without recording a choice.
            // Adopt the new default once; subsequent explicit Off choices persist.
            if (!TaskbarVisibilityConfigured) HideTaskbarsDuringCapture = true;
            // Earlier releases enabled tray hiding by default and saved no opt-in
            // provenance. Make Close exit normally until the current Settings UI
            // has explicitly accepted the notification-area choice.
            if (!TrayBehaviorConfirmed) KeepRunningInTray = false;
            if (String.IsNullOrWhiteSpace(ScreenshotFolder)) ScreenshotFolder = DefaultScreenshotFolder();
            if (String.IsNullOrWhiteSpace(RecordingFolder)) RecordingFolder = DefaultRecordingFolder();
            ScreenshotBorderWidth = Math.Max(1, Math.Min(20, ScreenshotBorderWidth));
            if (String.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase)) Theme = "Light";
            else if (String.Equals(Theme, "System", StringComparison.OrdinalIgnoreCase)) Theme = "System";
            else Theme = "Dark";
            if (String.Equals(AnnotationToolbarPosition, "Right", StringComparison.OrdinalIgnoreCase)) AnnotationToolbarPosition = "Right";
            else if (String.Equals(AnnotationToolbarPosition, "Bottom", StringComparison.OrdinalIgnoreCase)) AnnotationToolbarPosition = "Bottom";
            else if (String.Equals(AnnotationToolbarPosition, "Left", StringComparison.OrdinalIgnoreCase)) AnnotationToolbarPosition = "Left";
            else AnnotationToolbarPosition = "Top";
            if (EditorApplicationPath == null) EditorApplicationPath = "";
            if (String.IsNullOrWhiteSpace(EditorApplicationName)) EditorApplicationName = "Paint";
            if (DefaultMicrophoneDeviceId == null) DefaultMicrophoneDeviceId = "";
            if (DefaultSystemAudioDeviceId == null) DefaultSystemAudioDeviceId = "";
            if (String.IsNullOrWhiteSpace(TranslationServerUrl) || TranslationServerUrl.Length > 2048) TranslationServerUrl = "http://127.0.0.1:5000/";
            if (String.IsNullOrWhiteSpace(TranslationTargetLanguage) || TranslationTargetLanguage.Length > 32) TranslationTargetLanguage = "en";
            if (!String.Equals(TranslationProvider, "LibreTranslate", StringComparison.OrdinalIgnoreCase)) TranslationProvider = "Offline";
            else TranslationProvider = "LibreTranslate";
            if (TranslationSourceLanguage == null || TranslationSourceLanguage.Length > 32) TranslationSourceLanguage = "";
            if (RecentInkColors == null) RecentInkColors = new int[0];
            if (RecentInkColors.Length > 8) Array.Resize(ref RecentInkColors, 8);
            if (LastUpdateCheckUtc == null) LastUpdateCheckUtc = "";
        }

        private static string FileName
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipStudio", "preferences.xml"); }
        }

        public static Preferences Load()
        {
            lock (fileGate) return LoadCore();
        }

        private static Preferences LoadCore()
        {
            try
            {
                if (File.Exists(FileName))
                    using (FileStream stream = File.OpenRead(FileName))
                    {
                        Preferences preferences = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(stream);
                        preferences.Normalize();
                        return preferences;
                    }
            }
            catch (Exception) { }
            return new Preferences();
        }

        public void Save()
        {
            lock (fileGate)
            {
                // A window or Settings draft can predate an asynchronous check.
                // Merge only this metadata so saving it cannot move the date back.
                DateTime nowUtc = DateTime.UtcNow;
                DateTime checkedUtc = GetLastUpdateCheckUtc();
                if (checkedUtc > nowUtc) checkedUtc = DateTime.MinValue;
                if (latestUpdateCheckUtc > nowUtc) latestUpdateCheckUtc = DateTime.MinValue;
                if (latestUpdateCheckUtc > checkedUtc) checkedUtc = latestUpdateCheckUtc;
                DateTime diskUtc = ReadUpdateCheckUtc(FileName);
                if (diskUtc > nowUtc) diskUtc = DateTime.MinValue;
                if (diskUtc > checkedUtc) checkedUtc = diskUtc;
                LastUpdateCheckUtc = checkedUtc == DateTime.MinValue ? "" : FormatUpdateCheckUtc(checkedUtc);
                SaveCore();
            }
        }

        private void SaveCore()
        {
            string path = FileName;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream stream = File.Create(temporary))
                    new XmlSerializer(typeof(Preferences)).Serialize(stream, this);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
        }

        internal DateTime GetLastUpdateCheckUtc() { return ParseUpdateCheckUtc(LastUpdateCheckUtc); }

        internal static DateTime ParseUpdateCheckUtc(string value)
        {
            DateTime parsed;
            if (String.IsNullOrWhiteSpace(value) || !DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out parsed)) return DateTime.MinValue;
            return parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
        }

        private static string FormatUpdateCheckUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
            return utc.ToString("o", CultureInfo.InvariantCulture);
        }

        private static XmlDocument ReadPreferencesDocument(string path)
        {
            XmlDocument document = new XmlDocument();
            document.XmlResolver = null;
            document.PreserveWhitespace = true;
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, settings)) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "Preferences")
                throw new InvalidDataException("The preference document is unavailable.");
            return document;
        }

        private static DateTime ReadUpdateCheckUtc(string path)
        {
            try
            {
                if (!File.Exists(path)) return DateTime.MinValue;
                XmlDocument document = ReadPreferencesDocument(path);
                XmlNode node = document.DocumentElement.SelectSingleNode("LastUpdateCheckUtc");
                return node == null ? DateTime.MinValue : ParseUpdateCheckUtc(node.InnerText);
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        internal static void RecordUpdateCheckAttempt(DateTime checkedUtc)
        {
            if (checkedUtc == DateTime.MinValue) return;
            lock (fileGate)
            {
                DateTime nowUtc = DateTime.UtcNow;
                DateTime normalized = ParseUpdateCheckUtc(FormatUpdateCheckUtc(checkedUtc));
                // Recover after a clock correction instead of retaining a future
                // value that would make every reopened process check again.
                if (normalized > nowUtc) normalized = nowUtc;
                if (latestUpdateCheckUtc > nowUtc) latestUpdateCheckUtc = DateTime.MinValue;
                if (normalized > latestUpdateCheckUtc) latestUpdateCheckUtc = normalized;
                string path = FileName;
                if (!File.Exists(path))
                {
                    new Preferences { LastUpdateCheckUtc = FormatUpdateCheckUtc(latestUpdateCheckUtc) }.SaveCore();
                    return;
                }
                // Update only the timestamp, retaining saved settings and unknown
                // compatible fields instead of saving an older editor snapshot.
                XmlDocument document = ReadPreferencesDocument(path);
                XmlElement node = document.DocumentElement.SelectSingleNode("LastUpdateCheckUtc") as XmlElement;
                DateTime diskUtc = node == null ? DateTime.MinValue : ParseUpdateCheckUtc(node.InnerText);
                if (diskUtc > nowUtc) diskUtc = DateTime.MinValue;
                if (diskUtc >= latestUpdateCheckUtc) return;
                if (node == null)
                {
                    node = document.CreateElement("LastUpdateCheckUtc");
                    document.DocumentElement.AppendChild(node);
                }
                node.InnerText = FormatUpdateCheckUtc(latestUpdateCheckUtc);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true })) document.Save(writer);
                    File.Replace(temporary, path, null);
                }
                finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
            }
        }
    }
}
