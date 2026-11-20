using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace SnipStudio.WinUI;

/// <summary>WinUI settings are deliberately separate from stable Snip Studio.</summary>
public sealed class DesktopPreferences
{
    public string Theme { get; set; } = "System";
    public uint InkArgb { get; set; } = 0xFFE24046;
    public uint HighlighterArgb { get; set; } = 0xFFFFDD35;
    public double StrokeWidth { get; set; } = 3;
    public int CaptureDelaySeconds { get; set; }
    public bool RememberCaptureDelay { get; set; } = true;
    public bool CaptureInNewWindow { get; set; }
    public bool AskSaveEditedScreenshots { get; set; } = true;
    public bool AddScreenshotBorder { get; set; }
    public uint ScreenshotBorderArgb { get; set; } = 0xFF4DA3FF;
    public int ScreenshotBorderWidth { get; set; } = 2;
    public bool UseHdrColorCorrection { get; set; }
    public bool HideTaskbars { get; set; } = true;
    public bool KeepPrimaryTaskbarVisible { get; set; } = true;
    public bool RequireCtrlForWheelZoom { get; set; } = true;
    public bool ResizeToSnip { get; set; } = true;
    public bool StartCompact { get; set; } = true;
    public bool ShowTooltips { get; set; } = true;
    public string AnnotationToolbarPosition { get; set; } = "Top";
    public bool DefaultMicrophone { get; set; } = true;
    public bool DefaultSystemAudio { get; set; } = true;
    public string DefaultMicrophoneDeviceId { get; set; } = "";
    public string DefaultSystemAudioDeviceId { get; set; } = "";
    public bool AutoCopyRecordingChanges { get; set; } = true;
    public bool AutoSaveOriginalRecordings { get; set; } = true;
    public bool AskSaveEditedRecordings { get; set; } = true;
    public bool HotkeysEnabled { get; set; }
    public bool UsePrintScreen { get; set; }
    public bool KeepShortcutsAfterClose { get; set; }
    public bool KeepRunningInTray { get; set; }
    public bool StartupWithWindows { get; set; }
    public bool AutoCheckForUpdates { get; set; } = true;
    public string LastUpdateCheckUtc { get; set; } = "";
    public string EditorApplicationPath { get; set; } = "";
    public bool AutoCopyCaptures { get; set; } = true;
    public bool AutoSaveOriginalCaptures { get; set; }
    public string ScreenshotFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snip Studio WinUI3");
    public string RecordingFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Snip Studio WinUI3");
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public DesktopPreferences Clone() => JsonSerializer.Deserialize<DesktopPreferences>(JsonSerializer.Serialize(this))!;

    internal void Normalize()
    {
        Theme = Theme switch { "Light" => "Light", "Dark" => "Dark", _ => "System" };
        StrokeWidth = double.IsFinite(StrokeWidth) ? Math.Clamp(StrokeWidth, 1, 100) : 3;
        CaptureDelaySeconds = Math.Clamp(CaptureDelaySeconds, 0, 10);
        ScreenshotBorderWidth = Math.Clamp(ScreenshotBorderWidth, 1, 20);
        AnnotationToolbarPosition = AnnotationToolbarPosition switch
        {
            "Right" => "Right", "Bottom" => "Bottom", "Left" => "Left", _ => "Top"
        };
        DefaultMicrophoneDeviceId ??= "";
        DefaultSystemAudioDeviceId ??= "";
        LastUpdateCheckUtc ??= "";
        EditorApplicationPath ??= "";
        if (string.IsNullOrWhiteSpace(ScreenshotFolder)) ScreenshotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snip Studio WinUI3");
        if (string.IsNullOrWhiteSpace(RecordingFolder)) RecordingFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Snip Studio WinUI3");
    }
}

/// <summary>Bounded, atomic local persistence; recent paths never leave this PC.</summary>
public sealed class DesktopPreferencesStore
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly int[] ReplaceConflictDelaysMilliseconds = { 10, 20, 40, 80, 160 };
    private readonly object gate = new();
    private readonly string path;
    private readonly string mutexName;
    private Envelope current = new();
    private DesktopPreferences baselinePreferences = new();
    private bool loaded;
    private bool primaryReadable;
    public string? LoadWarning { get; private set; }

    public DesktopPreferencesStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipStudio.WinUI3", "preferences.json")) { }

    public DesktopPreferencesStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        if (!Path.IsPathFullyQualified(settingsPath)) throw new ArgumentException("The settings path must be absolute.", nameof(settingsPath));
        path = Path.GetFullPath(settingsPath);
        mutexName = "Local\\SnipStudio.WinUI3.Settings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
    }

    public DesktopPreferences Load()
    {
        lock (gate)
        {
            using IDisposable fileLock = LockFile();
            EnsureLoaded();
            baselinePreferences = current.Preferences.Clone();
            return current.Preferences.Clone();
        }
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        primaryReadable = TryLoad(path, out current);
        if (!primaryReadable)
        {
            bool hasPrimary = File.Exists(path);
            bool backupRecovered = TryLoad(path + ".bak", out current);
            if (hasPrimary || backupRecovered)
                LoadWarning = backupRecovered ? "Settings were recovered from the last saved backup. Review them before saving." : "Saved settings could not be read. Defaults are active; the existing file has been kept.";
        }
        current.Preferences ??= new();
        current.Preferences.Normalize();
        current.RecentPaths = NormalizeRecent(current.RecentPaths);
        baselinePreferences = current.Preferences.Clone();
    }

    public IReadOnlyList<string> RecentPaths
    {
        get
        {
            lock (gate)
            {
                using IDisposable fileLock = LockFile();
                EnsureLoaded();
                current = ReadLatest();
                // Refreshing history must not silently reset the baseline of
                // an open Settings draft in this window.
                return current.RecentPaths.ToArray();
            }
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (gate)
        {
            using IDisposable fileLock = LockFile();
            EnsureLoaded();
            DesktopPreferences copy = preferences.Clone();
            copy.Normalize();
            Envelope latest = ReadLatest();
            // A second editor can save unrelated preferences while this dialog
            // is open. Apply only fields changed from our loaded baseline.
            JsonObject baseline = JsonSerializer.SerializeToNode(baselinePreferences)!.AsObject();
            JsonObject incoming = JsonSerializer.SerializeToNode(copy)!.AsObject();
            JsonObject merged = JsonSerializer.SerializeToNode(latest.Preferences)!.AsObject();
            foreach (var property in incoming)
            {
                if (!baseline.TryGetPropertyValue(property.Key, out JsonNode? old) || !JsonNode.DeepEquals(old, property.Value))
                    merged[property.Key] = property.Value?.DeepClone();
            }
            copy = merged.Deserialize<DesktopPreferences>()!;
            copy.Normalize();
            Envelope next = new() { Preferences = copy, RecentPaths = latest.RecentPaths.ToList(), AdditionalData = latest.AdditionalData };
            Write(next);
            current = next;
            baselinePreferences = copy.Clone();
            LoadWarning = null;
        }
    }

    public void AddRecent(string filePath)
    {
        string normalized = NormalizePath(filePath) ?? throw new ArgumentException("Recent snips require an absolute image, PDF or recording path.", nameof(filePath));
        lock (gate)
        {
            using IDisposable fileLock = LockFile();
            EnsureLoaded();
            Envelope latest = ReadLatest();
            var recent = latest.RecentPaths.Where(item => !string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            recent.Insert(0, normalized);
            Envelope next = new() { Preferences = latest.Preferences, RecentPaths = recent.Take(32).ToList(), AdditionalData = latest.AdditionalData };
            Write(next);
            current = next;
        }
    }

    public void ClearRecent()
    {
        lock (gate)
        {
            using IDisposable fileLock = LockFile();
            EnsureLoaded();
            Envelope latest = ReadLatest();
            Envelope next = new() { Preferences = latest.Preferences, AdditionalData = latest.AdditionalData };
            Write(next);
            current = next;
        }
    }

    private Envelope ReadLatest()
    {
        // An unavailable reader is not corrupt data and cannot justify writing
        // an older in-memory envelope over a newer file. Save must surface that
        // error while keeping the draft; only missing/invalid JSON uses recovery.
        primaryReadable = TryLoad(path, out Envelope latest, failIfUnavailable: true);
        if (!primaryReadable && !TryLoad(path + ".bak", out latest, failIfUnavailable: true)) return current;
        // Re-read the recoverable backup under the writer mutex as well. A
        // missing primary is not evidence that a newer backup can be ignored,
        // and an unreadable backup must not be replaced by stale defaults.
        latest.Preferences ??= new();
        latest.Preferences.Normalize();
        latest.RecentPaths = NormalizeRecent(latest.RecentPaths);
        return latest;
    }

    private IDisposable LockFile()
    {
        // Readers and writers share this lease: ReplaceFile can briefly own
        // exclusive primary/backup handles while publishing, so delete sharing
        // alone cannot guarantee a fresh reader can open a coherent snapshot.
        var mutex = new Mutex(initiallyOwned: false, name: mutexName);
        bool acquired;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another Snip Studio window is saving settings. Try again; your changes have been kept.");
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private void Write(Envelope next)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumBytes) throw new IOException("Settings exceed the 1 MB limit; the saved file has been kept.");
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                // Keep an unreadable primary for diagnosis and retain any good
                // recovery backup rather than replacing it with corrupt bytes.
                if (!primaryReadable)
                    File.Copy(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json", overwrite: false);
                ReplaceWithSharingRetry(temporary, path, primaryReadable ? path + ".bak" : null);
            }
            else File.Move(temporary, path);
            primaryReadable = true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReplaceWithSharingRetry(string source, string destination, string? backup)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(source, destination, backup);
                return;
            }
            catch (IOException error) when (attempt < ReplaceConflictDelaysMilliseconds.Length &&
                IsReplaceSharingConflict(error) && File.Exists(source) && File.Exists(destination))
            {
                // Keep the same flushed source, destination and backup. Windows
                // documents unchanged source/destination names for error 1175
                // and sharing/locking errors. A brief scanner/reader lease can
                // therefore be retried without delete/move/overwrite fallbacks.
                // The five waits total 310 ms; persistent conflicts still fail.
                Thread.Sleep(ReplaceConflictDelaysMilliseconds[attempt]);
            }
        }
    }

    private static bool IsReplaceSharingConflict(IOException error) => error.HResult is
        unchecked((int)0x80070020) or // ERROR_SHARING_VIOLATION
        unchecked((int)0x80070021) or // ERROR_LOCK_VIOLATION
        unchecked((int)0x80070497);   // ERROR_UNABLE_TO_REMOVE_REPLACED (1175)

    private static bool TryLoad(string candidate, out Envelope value, bool failIfUnavailable = false)
    {
        value = new();
        try
        {
            // Replacement can rename an open reader safely while this handle
            // keeps its original bytes. In-place writers may not alter a JSON
            // snapshot during parsing; new atomic saves remain allowed.
            using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumBytes) return false;
            value = JsonSerializer.Deserialize<Envelope>(stream, new JsonSerializerOptions { MaxDepth = 32 }) ?? new();
            return true;
        }
        catch (IOException error) when (!failIfUnavailable || error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) when (!failIfUnavailable) { return false; }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return false; }
    }

    private static List<string> NormalizeRecent(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths is null) return result;
        int scanned = 0;
        foreach (string candidate in paths)
        {
            if (++scanned > 128) break;
            string? normalized = NormalizePath(candidate);
            if (normalized is not null && seen.Add(normalized)) result.Add(normalized);
            if (result.Count == 32) break;
        }
        return result;
    }

    private static string? NormalizePath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 32700 || !Path.IsPathFullyQualified(candidate) || candidate.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0) return null;
        try
        {
            string fullPath = Path.GetFullPath(candidate);
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".pdf" or ".mp4" or ".wmv" ? fullPath : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private sealed class Envelope
    {
        public Envelope() { }
        public DesktopPreferences Preferences { get; set; } = new();
        public List<string> RecentPaths { get; set; } = new();
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }

    private sealed class MutexLease : IDisposable
    {
        private Mutex? mutex;
        internal MutexLease(Mutex mutex) { this.mutex = mutex; }
        public void Dispose()
        {
            Mutex? owned = Interlocked.Exchange(ref mutex, null);
            if (owned is null) return;
            try { owned.ReleaseMutex(); }
            finally { owned.Dispose(); }
        }
    }
}
