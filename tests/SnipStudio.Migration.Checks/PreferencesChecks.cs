using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnipStudio.WinUI;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckPreferencesAsync(string output)
    {
        string folder = Path.Combine(output, "preferences");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "merge.json");
        var first = new DesktopPreferencesStore(path);
        var second = new DesktopPreferencesStore(path);
        DesktopPreferences firstEdit = first.Load(), secondEdit = second.Load();
        Check(!File.Exists(path) && first.LoadWarning is null && firstEdit.Theme == "System" && firstEdit.StrokeWidth == 3, "Reading fresh settings uses defaults without writing a file");
        Check(firstEdit.AutoCopyCaptures, "New profiles automatically copy captures and committed edits");
        string clipboardPath = Path.Combine(folder, "clipboard-choice.json");
        File.WriteAllText(clipboardPath, "{\"Preferences\":{\"AutoCopyCaptures\":false},\"RecentPaths\":[]}");
        Check(!new DesktopPreferencesStore(clipboardPath).Load().AutoCopyCaptures, "An existing clipboard opt-out survives the new default");
        firstEdit.Theme = "Dark";
        Check(first.Load().Theme == "System", "Unsaved settings clone cannot change stored preferences");
        first.Save(firstEdit);
        secondEdit.StrokeWidth = 7.5;
        second.Save(secondEdit);
        DesktopPreferences merged = new DesktopPreferencesStore(path).Load();
        Check(merged.Theme == "Dark" && merged.StrokeWidth == 7.5, "Two stale settings stores merge unrelated changed fields");
        Check(secondEdit.Theme == "System" && secondEdit.StrokeWidth == 7.5, "Saving does not mutate the caller's settings object");

        var stale = new DesktopPreferencesStore(path);
        DesktopPreferences staleEdit = stale.Load();
        staleEdit.Theme = "Light";
        var later = new DesktopPreferencesStore(path);
        DesktopPreferences laterEdit = later.Load();
        laterEdit.StrokeWidth = 18;
        later.Save(laterEdit);
        string recent = Path.Combine(folder, "00007_Aa.pdf");
        stale.AddRecent(recent);
        stale.Save(staleEdit);
        var afterRecent = new DesktopPreferencesStore(path);
        DesktopPreferences afterRecentPreferences = afterRecent.Load();
        Check(afterRecentPreferences.Theme == "Light" && afterRecentPreferences.StrokeWidth == 18 && afterRecent.RecentPaths.SequenceEqual(new[] { recent }),
            "Recent-list refresh cannot advance a stale dialog baseline and overwrite another editor's preference");

        string parallelPath = Path.Combine(folder, "parallel.json");
        string[] parallelRecent = Enumerable.Range(0, 8).Select(index => Path.Combine(folder, $"parallel-{index:0000}.png")).ToArray();
        Task[] recentWrites = parallelRecent.Select(value => Task.Run(() => new DesktopPreferencesStore(parallelPath).AddRecent(value))).ToArray();
        await Task.WhenAll(recentWrites);
        var parallelResult = new DesktopPreferencesStore(parallelPath);
        Check(parallelResult.RecentPaths.Count == parallelRecent.Length && new HashSet<string>(parallelResult.RecentPaths, StringComparer.OrdinalIgnoreCase).SetEquals(parallelRecent),
            "Concurrent recent writes from independent stores retain every unique file");
        string recased = Path.Combine(folder, "PARALLEL-0000.PNG");
        parallelResult.AddRecent(recased);
        Check(parallelResult.RecentPaths.Count == parallelRecent.Length && parallelResult.RecentPaths[0] == recased,
            "Case-insensitive recent deduplication preserves the newest exact path spelling");
        IReadOnlyList<string> detached = parallelResult.RecentPaths;
        parallelResult.ClearRecent();
        Check(detached.Count == parallelRecent.Length && parallelResult.RecentPaths.Count == 0, "Recent-path snapshots are detached from later clearing");
        for (int index = 0; index < 36; index++) parallelResult.AddRecent(Path.Combine(folder, $"bounded-{index:0000}.pdf"));
        Check(parallelResult.RecentPaths.Count == 32 && Path.GetFileName(parallelResult.RecentPaths[0]) == "bounded-0035.pdf" && Path.GetFileName(parallelResult.RecentPaths[^1]) == "bounded-0004.pdf",
            "Recent-list bound retains the 32 most recently added files in order");
        byte[] beforeInvalid = File.ReadAllBytes(parallelPath);
        foreach (string invalid in new[] { "relative.png", Path.Combine(folder, "untrusted.exe"), Path.Combine(folder, "line\nbreak.png"), "" })
            Expect<ArgumentException>(() => parallelResult.AddRecent(invalid), "Invalid recent path is rejected without writing: " + invalid.Replace("\n", "<newline>"));
        Expect<ArgumentException>(() => new DesktopPreferencesStore("relative-settings.json"), "Relative settings destination is rejected");
        Check(File.ReadAllBytes(parallelPath).SequenceEqual(beforeInvalid), "Invalid recent paths preserve every existing settings byte");
        CheckRecentHistoryPolicy();

        string videoRecentPath = Path.Combine(folder, "recent-videos.json");
        var videoRecent = new DesktopPreferencesStore(videoRecentPath);
        foreach (string extension in new[] { ".MOV", ".avi", ".m4v", ".webm", ".mkv", ".snipstudio" })
        {
            string video = Path.Combine(folder, "00007_Aa" + extension);
            videoRecent.AddRecent(video);
            Check(new DesktopPreferencesStore(videoRecentPath).RecentPaths[0] == video,
                "A supported recent video round-trips with its exact path: " + extension);
        }

        string inkPath = Path.Combine(folder, "remembered-ink.json");
        var openSettings = new DesktopPreferencesStore(inkPath);
        DesktopPreferences openDraft = openSettings.Load();
        openDraft.Theme = "Dark";
        openDraft.LastScreenshotCaptureKind = CaptureKind.Freeform;
        var inkStore = new DesktopPreferencesStore(inkPath);
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => new DesktopPreferencesStore(inkPath).RememberInk((uint)index, false, false))));
        Check(new DesktopPreferencesStore(inkPath).Load().RecentInkColors.ToHashSet()
            .SetEquals(Enumerable.Range(0, 8).Select(index => (uint)index)), "Concurrent ink changes retain all eight swatches");
        DesktopPreferences detachedInk = inkStore.RememberInk(1, true, false);
        detachedInk.RecentInkColors[0] = 99;
        Check(inkStore.Load().RecentInkColors[0] == 1, "Remembered ink snapshots cannot mutate persisted state");
        inkStore.RememberInk(8, false, true);
        string lastSaveFolder = Path.Combine(folder, "other-output");
        inkStore.RememberLastSaveFolder(lastSaveFolder);
        openSettings.Save(openDraft);
        DesktopPreferences rememberedChoices = new DesktopPreferencesStore(inkPath).Load();
        Check(rememberedChoices.Theme == "Dark" && rememberedChoices.LastScreenshotCaptureKind == CaptureKind.Freeform
            && rememberedChoices.InkArgb == 1 && rememberedChoices.HighlighterArgb == 8
            && rememberedChoices.LastSaveFolder == lastSaveFolder && rememberedChoices.RecentInkColors is [8, 1, ..]
            && rememberedChoices.RecentInkColors.Length == 8,
            "Saving an older Settings draft retains recent colors and Save As folder while persisting capture shape");

        CheckUnknownPreferenceJson(folder);
        CheckSettingsRecovery(folder);
        CheckIncompleteSettingsRecovery(folder);
        CheckStartupEntryOwnership();
        CheckSettingsWriteFailure(folder);
        CheckSettingsBackupRefresh(folder);
        await CheckSettingsAtomicSharingAsync(folder);
        await CheckSettingsConcurrentRepetitionAsync(folder);
    }

    private static void CheckUnknownPreferenceJson(string folder)
    {
        string path = Path.Combine(folder, "unknown.json");
        File.WriteAllText(path, """
            {"Preferences":{"Theme":"System","StrokeWidth":3,"FutureSetting":{"Identifier":"00007_Aa","Rows":["same","same"]}},"RecentPaths":[],"FutureEnvelope":{"Marker":"000042","Case":"Aa"}}
            """, new UTF8Encoding(false));
        var first = new DesktopPreferencesStore(path);
        var second = new DesktopPreferencesStore(path);
        DesktopPreferences one = first.Load(), two = second.Load();
        one.Theme = "Dark";
        first.Save(one);
        JsonObject latest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        latest["Preferences"]!["LaterFeature"] = JsonNode.Parse("{\"Quantity\":\"0007\",\"Duplicates\":[1,1]}");
        File.WriteAllText(path, latest.ToJsonString(), new UTF8Encoding(false));
        two.CaptureDelaySeconds = 4;
        second.Save(two);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement prefs = document.RootElement.GetProperty("Preferences");
        Check(prefs.GetProperty("Theme").GetString() == "Dark" && prefs.GetProperty("CaptureDelaySeconds").GetInt32() == 4,
            "Stale preference save preserves a newer unrelated preference");
        Check(prefs.GetProperty("FutureSetting").GetProperty("Identifier").GetString() == "00007_Aa" &&
            prefs.GetProperty("FutureSetting").GetProperty("Rows").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "same", "same" }),
            "Unknown nested preference JSON preserves identifiers and duplicate values");
        Check(prefs.GetProperty("LaterFeature").GetProperty("Quantity").GetString() == "0007" && prefs.GetProperty("LaterFeature").GetProperty("Duplicates").GetArrayLength() == 2,
            "A feature added after the dialog loaded is not removed by a stale save");
        Check(document.RootElement.GetProperty("FutureEnvelope").GetProperty("Marker").GetString() == "000042" && document.RootElement.GetProperty("FutureEnvelope").GetProperty("Case").GetString() == "Aa",
            "Unknown top-level envelope JSON survives settings merges");
    }

    private static void CheckSettingsRecovery(string folder)
    {
        string path = Path.Combine(folder, "recovery.json");
        var initial = new DesktopPreferencesStore(path);
        DesktopPreferences edit = initial.Load();
        edit.Theme = "Dark"; initial.Save(edit);
        edit = initial.Load(); edit.StrokeWidth = 5; initial.Save(edit);
        byte[] goodBackup = File.ReadAllBytes(path + ".bak");
        byte[] corruptPrimary = Encoding.UTF8.GetBytes("{corrupt-primary-0007}");
        File.WriteAllBytes(path, corruptPrimary);
        var recovery = new DesktopPreferencesStore(path);
        DesktopPreferences recovered = recovery.Load();
        Check(recovery.LoadWarning is not null && recovered.Theme == "Dark" && recovered.StrokeWidth == 3,
            "Corrupt primary settings recover the actual prior saved backup and report recovery");
        Check(File.ReadAllBytes(path).SequenceEqual(corruptPrimary) && File.ReadAllBytes(path + ".bak").SequenceEqual(goodBackup),
            "Recovery reads preserve corrupt primary and good backup bytes until an explicit save");
        recovered.CaptureDelaySeconds = 3;
        recovery.Save(recovered);
        string[] unreadable = Directory.GetFiles(folder, "recovery.json.unreadable-*.json");
        Check(unreadable.Length == 1 && File.ReadAllBytes(unreadable[0]).SequenceEqual(corruptPrimary), "Saving recovered preferences preserves the unreadable primary as evidence");
        Check(File.ReadAllBytes(path + ".bak").SequenceEqual(goodBackup), "Saving recovered preferences does not replace the good backup with corrupt bytes");
        var saved = new DesktopPreferencesStore(path);
        DesktopPreferences savedPreferences = saved.Load();
        Check(saved.LoadWarning is null && savedPreferences.Theme == "Dark" && savedPreferences.CaptureDelaySeconds == 3,
            "Saved recovered preferences reopen as a readable current document");

        string doublePath = Path.Combine(folder, "both-corrupt.json");
        byte[] badPrimary = Encoding.UTF8.GetBytes("not-json-primary"), badBackup = Encoding.UTF8.GetBytes("not-json-backup");
        File.WriteAllBytes(doublePath, badPrimary); File.WriteAllBytes(doublePath + ".bak", badBackup);
        var defaultsStore = new DesktopPreferencesStore(doublePath);
        DesktopPreferences defaults = defaultsStore.Load();
        Check(defaults.Theme == "System" && defaults.StrokeWidth == 3 && defaultsStore.LoadWarning is not null,
            "Corrupt primary and backup activate disclosed defaults rather than inventing saved values");
        Check(File.ReadAllBytes(doublePath).SequenceEqual(badPrimary) && File.ReadAllBytes(doublePath + ".bak").SequenceEqual(badBackup),
            "Reading defaults preserves both unreadable files");
        defaults.Theme = "Light"; defaultsStore.Save(defaults);
        Check(new DesktopPreferencesStore(doublePath).Load().Theme == "Light" && File.ReadAllBytes(doublePath + ".bak").SequenceEqual(badBackup),
            "Explicitly saving defaults recovers the primary while retaining the old backup");
    }

    private static void CheckIncompleteSettingsRecovery(string folder)
    {
        foreach ((string label, string incomplete) in new[]
        {
            ("null-envelope", "null"),
            ("missing-preferences", """{"RecentPaths":[]}"""),
            ("null-preferences", """{"Preferences":null,"RecentPaths":[]}""")
        })
        {
            string path = Path.Combine(folder, label + ".json");
            string backupPath = path + ".bak";
            string marker = Path.Combine(folder, "00004_Kept.pdf");
            string opened = Path.Combine(folder, "00005_New.png");
            byte[] goodBackup = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Preferences = new { Theme = "Dark", StrokeWidth = 13.5 },
                RecentPaths = new[] { marker },
                FutureEnvelope = new { Identifier = "00004" }
            }));
            byte[] badPrimary = Encoding.UTF8.GetBytes(incomplete);
            File.WriteAllBytes(backupPath, goodBackup);
            File.WriteAllBytes(path, badPrimary);

            var store = new DesktopPreferencesStore(path);
            DesktopPreferences loaded = store.Load();
            Check(store.LoadWarning is not null && loaded.Theme == "Dark" && loaded.StrokeWidth == 13.5 &&
                store.RecentPaths.SequenceEqual(new[] { marker }),
                label + " restores real values from the saved backup instead of treating incomplete JSON as defaults");
            Check(File.ReadAllBytes(path).SequenceEqual(badPrimary) && File.ReadAllBytes(backupPath).SequenceEqual(goodBackup),
                label + " recovery reads leave both files intact");

            store.AddRecent(opened);
            var reopened = new DesktopPreferencesStore(path);
            DesktopPreferences recovered = reopened.Load();
            Check(reopened.LoadWarning is null && recovered.Theme == "Dark" && recovered.StrokeWidth == 13.5 &&
                reopened.RecentPaths.SequenceEqual(new[] { opened, marker }),
                label + " automatic recent-path publication keeps recovered settings and history");
            using JsonDocument written = JsonDocument.Parse(File.ReadAllBytes(path));
            Check(written.RootElement.GetProperty("FutureEnvelope").GetProperty("Identifier").GetString() == "00004" &&
                File.ReadAllBytes(backupPath).SequenceEqual(goodBackup),
                label + " publication preserves unknown backup fields and never rotates the good backup out");
            string archived = Directory.GetFiles(folder, label + ".json.unreadable-*.json").Single();
            Check(File.ReadAllBytes(archived).SequenceEqual(badPrimary), label + " incomplete primary is archived unchanged");
        }

        string emptyPath = Path.Combine(folder, "empty-preferences.json");
        File.WriteAllText(emptyPath, """{"Preferences":{},"RecentPaths":[]}""");
        var empty = new DesktopPreferencesStore(emptyPath);
        Check(empty.Load().Theme == "System" && empty.LoadWarning is null,
            "A present empty preferences object still loads with defaults for optional existing fields");

        string onlyBackupPath = Path.Combine(folder, "unreadable-backup-only.json");
        File.WriteAllText(onlyBackupPath + ".bak", "null");
        var unreadableBackup = new DesktopPreferencesStore(onlyBackupPath);
        Check(unreadableBackup.Load().Theme == "System" && unreadableBackup.LoadWarning is not null &&
            !File.Exists(onlyBackupPath) && File.ReadAllText(onlyBackupPath + ".bak") == "null",
            "A corrupt backup without a primary reports defaults and leaves the backup unchanged");
    }

    private static void CheckStartupEntryOwnership()
    {
        // Exercise the predicate shared by enable, disable and rollback.
        // These fixtures never read or write the actual Run registry key.
        var canChange = typeof(DesktopIntegrationService)
            .GetMethod("StartupValueCanChange", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<object?, string, bool>>();
        const string command = "\"C:\\Preview\\SnipStudio.WinUI3.Preview.exe\" --background";
        Check(canChange(null, command), "A missing startup value allows registration");
        Check(canChange(command, command) && canChange(command.ToUpperInvariant(), command),
            "The current installation owns its exact startup command, including ordinary Windows path case variations");
        Check(!canChange(command.Replace(@"C:\Preview", @"C:\Installed", StringComparison.Ordinal), command),
            "Enabling or restoring startup cannot overwrite another installation's registration");
        Check(!canChange(command.Replace("--background", "--different", StringComparison.Ordinal), command),
            "A different registered command line is not treated as this installation's registration");
        Check(!canChange(7, command) && !canChange("", command), "An occupied unsupported registry value is preserved");
    }

    private static void CheckSettingsWriteFailure(string folder)
    {
        string path = Path.Combine(folder, "locked.json");
        var store = new DesktopPreferencesStore(path);
        DesktopPreferences preferences = store.Load();
        preferences.Theme = "Dark"; store.Save(preferences);
        byte[] original = File.ReadAllBytes(path);
        preferences = store.Load(); preferences.Theme = "Light";
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Expect<IOException>(() => store.Save(preferences), "Locked settings destination reports a save failure");
        Check(File.ReadAllBytes(path).SequenceEqual(original) && preferences.Theme == "Light", "Failed settings write preserves disk bytes and the caller's unsaved changes");
        Check(!Directory.GetFiles(folder, "locked.json.*.tmp").Any(), "Failed settings write cleans its temporary file");
        Check(!Directory.GetFiles(folder, "locked.json.unreadable-*.json").Any(), "An unavailable settings read is not treated as corrupt data to archive or overwrite");
        store.Save(preferences);
        Check(new DesktopPreferencesStore(path).Load().Theme == "Light", "Settings save retries after the destination becomes available");
    }

    private static void PrepareSettingsWorkerFixture(string output)
    {
        string folder = Path.Combine(output, "settings-workers");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, """
            {"Preferences":{"Theme":"System","StrokeWidth":3,"FutureSetting":{"Identifier":"00007","Rows":["Aa","Aa"]}},"RecentPaths":[],"FutureEnvelope":{"Marker":"000042"}}
            """, new UTF8Encoding(false));
        foreach (string id in new[] { "theme", "width" })
        {
            var task = new SettingsWorkerTask
            {
                SettingsPath = path,
                ReadyPath = Path.Combine(folder, id + ".ready.json"),
                ReleasePath = Path.Combine(folder, id + ".release"),
                ReceiptPath = Path.Combine(folder, id + ".receipt.json"),
                Theme = id == "theme" ? "Dark" : null,
                StrokeWidth = id == "width" ? 8.5 : null,
                RecentPath = Path.Combine(folder, id == "theme" ? "00007_Aa.png" : "00007_Aa.pdf"),
                WaitMilliseconds = 15000
            };
            File.WriteAllText(Path.Combine(folder, id + ".task.json"), JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(folder, "STATUS.txt"), "PREPARED ONLY. No cross-process test has run. Launch two signed/preverified check instances with --settings-worker <theme.task.json|width.task.json>; wait for both ready files, create release markers, await exit, then run --verify-settings-workers <this folder>.\n", new UTF8Encoding(false));
    }

    private sealed class SettingsWorkerTask
    {
        public SettingsWorkerTask() { }
        public string SettingsPath { get; set; } = "";
        public string ReadyPath { get; set; } = "";
        public string ReleasePath { get; set; } = "";
        public string ReceiptPath { get; set; } = "";
        public string? Theme { get; set; }
        public double? StrokeWidth { get; set; }
        public string RecentPath { get; set; } = "";
        public int WaitMilliseconds { get; set; } = 15000;
    }

    private static int RunSettingsWorker(string taskFile)
    {
        SettingsWorkerTask? task = null;
        bool taskValidated = false;
        string? error = null;
        bool passed = false;
        DesktopPreferences? baseline = null, result = null;
        try
        {
            if (!Path.IsPathFullyQualified(taskFile)) throw new ArgumentException("Use an absolute worker task path.");
            string input = Path.GetFullPath(taskFile);
            string folder = Path.GetDirectoryName(input)!;
            if (new FileInfo(input).Length > 64 * 1024) throw new ArgumentException("The worker task exceeds 64 KB.");
            task = JsonSerializer.Deserialize<SettingsWorkerTask>(File.ReadAllBytes(input)) ?? throw new ArgumentException("The worker task is empty.");
            foreach (string path in new[] { task.SettingsPath, task.ReadyPath, task.ReleasePath, task.ReceiptPath, task.RecentPath })
                RequireWorkerLocalPath(folder, path);
            if (new[] { input, task.SettingsPath, task.ReadyPath, task.ReleasePath, task.ReceiptPath, task.RecentPath }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 6)
                throw new ArgumentException("Worker input, settings, marker, receipt and recent paths must be distinct.");
            if (task.WaitMilliseconds is < 100 or > 20000) throw new ArgumentException("Worker wait must be between 100 and 20,000 milliseconds.");
            if (File.Exists(task.ReadyPath) || File.Exists(task.ReleasePath) || File.Exists(task.ReceiptPath)) throw new IOException("Use unused worker markers and receipt paths.");
            taskValidated = true;
            var store = new DesktopPreferencesStore(task.SettingsPath);
            DesktopPreferences edit = store.Load();
            baseline = edit.Clone();
            if (task.Theme is not null) edit.Theme = task.Theme;
            if (task.StrokeWidth.HasValue) edit.StrokeWidth = task.StrokeWidth.Value;
            File.WriteAllText(task.ReadyPath, JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, LoadedUtc = DateTimeOffset.UtcNow, Preferences = baseline }), new UTF8Encoding(false));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(task.ReleasePath))
            {
                if (wait.ElapsedMilliseconds >= task.WaitMilliseconds) throw new TimeoutException("The settings worker was not released within its bounded wait.");
                Thread.Sleep(25);
            }
            // Both workers have captured their stale dialog baseline before the
            // orchestrator releases them. AddRecent must not advance that base.
            store.AddRecent(task.RecentPath);
            store.Save(edit);
            result = new DesktopPreferencesStore(task.SettingsPath).Load();
            passed = true;
            return 0;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            string receipt = JsonSerializer.Serialize(new { CheckedUtc = DateTimeOffset.UtcNow, Passed = passed,
                ProcessId = Environment.ProcessId, Baseline = baseline, ObservedAfterSave = result, Failure = error }, new JsonSerializerOptions { WriteIndented = true });
            if (taskValidated && task is not null)
            {
                try { File.WriteAllText(task.ReceiptPath, receipt, new UTF8Encoding(false)); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or ArgumentException) { Console.Error.WriteLine(writeError.Message); }
            }
            Console.WriteLine(receipt);
        }
    }

    private static int VerifySettingsWorkers(string evidenceFolder)
    {
        string folder = Path.GetFullPath(evidenceFolder);
        var failures = new List<string>();
        var ids = new HashSet<int>();
        foreach (string id in new[] { "theme", "width" })
        {
            string receipt = Path.Combine(folder, id + ".receipt.json");
            try
            {
                using JsonDocument data = JsonDocument.Parse(File.ReadAllBytes(receipt));
                if (!data.RootElement.GetProperty("Passed").GetBoolean()) failures.Add(id + " worker failed");
                ids.Add(data.RootElement.GetProperty("ProcessId").GetInt32());
                JsonElement baseline = data.RootElement.GetProperty("Baseline");
                if (baseline.GetProperty("Theme").GetString() != "System" || baseline.GetProperty("StrokeWidth").GetDouble() != 3) failures.Add(id + " worker did not capture the original stale baseline");
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { failures.Add(id + " receipt unavailable/invalid: " + error.Message); }
        }
        if (ids.Count != 2) failures.Add("Workers were not two separate processes");
        try
        {
            string path = Path.Combine(folder, "settings.json");
            var store = new DesktopPreferencesStore(path);
            DesktopPreferences preferences = store.Load();
            if (preferences.Theme != "Dark" || preferences.StrokeWidth != 8.5) failures.Add("Cross-process per-field preference merge lost a change");
            var expectedRecent = new HashSet<string>(new[] { Path.Combine(folder, "00007_Aa.png"), Path.Combine(folder, "00007_Aa.pdf") }, StringComparer.OrdinalIgnoreCase);
            if (store.RecentPaths.Count != 2 || !expectedRecent.SetEquals(store.RecentPaths)) failures.Add("Cross-process recent merge lost a path");
            using JsonDocument data = JsonDocument.Parse(File.ReadAllBytes(path));
            if (data.RootElement.GetProperty("Preferences").GetProperty("FutureSetting").GetProperty("Identifier").GetString() != "00007" ||
                data.RootElement.GetProperty("Preferences").GetProperty("FutureSetting").GetProperty("Rows").GetArrayLength() != 2 ||
                data.RootElement.GetProperty("FutureEnvelope").GetProperty("Marker").GetString() != "000042") failures.Add("Cross-process save changed unknown JSON");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { failures.Add("Merged settings unavailable/invalid: " + error.Message); }
        string result = JsonSerializer.Serialize(new { CheckedUtc = DateTimeOffset.UtcNow, Passed = failures.Count == 0, WorkerProcessIds = ids,
            Scope = "Two separately launched signed/preverified check processes with stale preference snapshots; local settings merge and recent-path persistence only.", Failures = failures }, new JsonSerializerOptions { WriteIndented = true });
        string output = Path.Combine(folder, "cross-process-checks.json");
        if (File.Exists(output)) { Console.Error.WriteLine("Use a fresh worker evidence directory; cross-process result already exists."); return 2; }
        File.WriteAllText(output, result, new UTF8Encoding(false));
        Console.WriteLine(result);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void RequireWorkerLocalPath(string folder, string candidate)
    {
        if (!Path.IsPathFullyQualified(candidate) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("All worker task paths must be absolute and in the same isolated evidence directory.");
    }
}
