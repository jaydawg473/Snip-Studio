using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SnipStudio.WinUI;

internal static partial class Program
{
    private static void CheckSettingsBackupRefresh(string folder)
    {
        foreach (bool corruptPrimary in new[] { false, true })
        {
            string label = corruptPrimary ? "corrupt" : "missing";
            string path = Path.Combine(folder, "newer-backup-" + label + ".json");
            File.WriteAllText(path, """
                {"Preferences":{"Theme":"Dark","StrokeWidth":5,"CaptureDelaySeconds":0,"FutureSetting":{"Identifier":"older-0007"}},"RecentPaths":[],"FutureEnvelope":{"Marker":"older-000042"}}
                """, new UTF8Encoding(false));
            var stale = new DesktopPreferencesStore(path);
            DesktopPreferences draft = stale.Load();
            draft.CaptureDelaySeconds = 4;
            string recoveredRecent = Path.Combine(folder, "backup-recent-0007_Aa.pdf");
            byte[] newerBackup = NewerBackupBytes(recoveredRecent);
            File.WriteAllBytes(path + ".bak", newerBackup);
            byte[] badPrimary = Encoding.UTF8.GetBytes("{corrupt-after-dialog-load-0007}");
            if (corruptPrimary) File.WriteAllBytes(path, badPrimary); else File.Delete(path);
            stale.Save(draft);
            var reopened = new DesktopPreferencesStore(path);
            DesktopPreferences preferences = reopened.Load();
            Check(preferences.Theme == "Light" && preferences.StrokeWidth == 9 && preferences.CaptureDelaySeconds == 4,
                "Saving a stale dialog over a " + label + " primary merges its changed field onto the newest readable backup");
            Check(reopened.RecentPaths.SequenceEqual(new[] { recoveredRecent }), "Newest backup recent paths survive stale-save recovery: " + label);
            Check(File.ReadAllBytes(path + ".bak").SequenceEqual(newerBackup), "Stale-save recovery preserves the exact newer backup bytes: " + label);
            using (JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(path)))
                Check(saved.RootElement.GetProperty("Preferences").GetProperty("FutureSetting").GetProperty("Identifier").GetString() == "newer-0007_Aa" &&
                    saved.RootElement.GetProperty("Preferences").GetProperty("FutureSetting").GetProperty("Rows").GetArrayLength() == 2 &&
                    saved.RootElement.GetProperty("FutureEnvelope").GetProperty("Marker").GetString() == "newer-000042",
                    "Newest backup unknown JSON survives stale-save recovery: " + label);
            string[] unreadable = Directory.GetFiles(folder, Path.GetFileName(path) + ".unreadable-*.json");
            Check(corruptPrimary ? unreadable.Length == 1 && File.ReadAllBytes(unreadable[0]).SequenceEqual(badPrimary) : unreadable.Length == 0,
                "Recovery archives actual corruption and does not invent a corrupt file for a missing primary: " + label);
        }

        foreach (bool corruptPrimary in new[] { false, true })
        {
            string label = corruptPrimary ? "corrupt" : "missing";
            string path = Path.Combine(folder, "locked-backup-" + label + ".json");
            File.WriteAllText(path, """
                {"Preferences":{"Theme":"Dark","StrokeWidth":5,"CaptureDelaySeconds":0},"RecentPaths":[]}
                """, new UTF8Encoding(false));
            var stale = new DesktopPreferencesStore(path);
            DesktopPreferences draft = stale.Load(); draft.CaptureDelaySeconds = 7;
            byte[] backup = NewerBackupBytes(Path.Combine(folder, "locked-backup-recent-0007_Aa.pdf"));
            File.WriteAllBytes(path + ".bak", backup);
            byte[] badPrimary = Encoding.UTF8.GetBytes("{corrupt-before-locked-backup-0007}");
            if (corruptPrimary) File.WriteAllBytes(path, badPrimary); else File.Delete(path);
            using (FileStream held = new(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.None))
                Expect<IOException>(() => stale.Save(draft), "Unreadable latest backup prevents a stale save over a " + label + " primary");
            Check(corruptPrimary ? File.ReadAllBytes(path).SequenceEqual(badPrimary) : !File.Exists(path),
                "Locked latest backup cannot create or overwrite the primary from stale in-memory settings: " + label);
            Check(File.ReadAllBytes(path + ".bak").SequenceEqual(backup) && draft.CaptureDelaySeconds == 7,
                "Locked-backup failure preserves backup bytes and unsaved draft: " + label);
            Check(!Directory.GetFiles(folder, Path.GetFileName(path) + ".*.tmp").Any() &&
                !Directory.GetFiles(folder, Path.GetFileName(path) + ".unreadable-*.json").Any(),
                "Unavailable backup read creates neither temporary output nor false corruption archive: " + label);
            stale.Save(draft);
            DesktopPreferences recovered = new DesktopPreferencesStore(path).Load();
            Check(recovered.Theme == "Light" && recovered.StrokeWidth == 9 && recovered.CaptureDelaySeconds == 7,
                "Unlocking the latest backup permits recovery with the retained user draft: " + label);
        }
    }

    private static byte[] NewerBackupBytes(string recent) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        Preferences = new
        {
            Theme = "Light", StrokeWidth = 9, CaptureDelaySeconds = 0,
            FutureSetting = new { Identifier = "newer-0007_Aa", Rows = new[] { "same", "same" } }
        },
        RecentPaths = new[] { recent },
        FutureEnvelope = new { Marker = "newer-000042" }
    });

    private static int RunSettingsRecoveryChecks(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use a new empty settings-recovery evidence directory.");
            return 2;
        }
        Directory.CreateDirectory(output);
        bool passed = false;
        string? failure = null;
        Stopwatch clock = Stopwatch.StartNew();
        try { CheckSettingsBackupRefresh(output); passed = true; return 0; }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); return 1; }
        finally
        {
            clock.Stop();
            string json = JsonSerializer.Serialize(new { CheckedUtc = DateTimeOffset.UtcNow, Passed = passed, Assertions = assertions,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Scope = "Isolated newest-backup merge, exact data preservation, unavailable-backup failure and draft retry; overlaps the same default-suite group and must not be added again to its assertion total.", Failure = failure },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "settings-recovery-checks.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
        }
    }
}
