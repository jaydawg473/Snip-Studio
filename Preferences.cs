using System;
using System.Drawing;
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
        public bool HideTaskbarsDuringCapture = false;
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
        public bool ShowTooltips = true;
        public bool StartCompact = true;
        public bool ResizeToSnip = true;
        public bool UsePrintScreen = true;
        public bool StartupWithWindows = false;
        public bool KeepRunningInTray = false;
        public bool TrayBehaviorConfirmed = false;
        public string EditorApplicationPath = DefaultEditorPath();
        public string EditorApplicationName = "Paint";

        // Retain settings written by another compatible version rather than silently
        // dropping them when this version changes one of its own preferences.
        [XmlAnyElement] public XmlElement[] AdditionalSettings;

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
            if (EditorApplicationPath == null) EditorApplicationPath = "";
            if (String.IsNullOrWhiteSpace(EditorApplicationName)) EditorApplicationName = "Paint";
            if (DefaultMicrophoneDeviceId == null) DefaultMicrophoneDeviceId = "";
            if (DefaultSystemAudioDeviceId == null) DefaultSystemAudioDeviceId = "";
            if (RecentInkColors == null) RecentInkColors = new int[0];
            if (RecentInkColors.Length > 8) Array.Resize(ref RecentInkColors, 8);
        }

        private static string FileName
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipStudio", "preferences.xml"); }
        }

        public static Preferences Load()
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
    }
}
