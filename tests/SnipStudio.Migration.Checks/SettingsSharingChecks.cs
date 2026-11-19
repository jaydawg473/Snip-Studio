using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SnipStudio.WinUI;

internal static partial class Program
{
    private static async Task CheckSettingsAtomicSharingAsync(string folder)
    {
        string path = Path.Combine(folder, "sharing.json");
        var store = new DesktopPreferencesStore(path);
        DesktopPreferences initial = store.Load();
        initial.Theme = "Dark"; store.Save(initial);
        DesktopPreferences width = store.Load(); width.StrokeWidth = 6; store.Save(width);
        byte[] original = File.ReadAllBytes(path);
        using (FileStream primaryReader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            DesktopPreferences edit = store.Load(); edit.CaptureDelaySeconds = 4; store.Save(edit);
            Check(ReadHeldStream(primaryReader).SequenceEqual(original), "A delete-sharing primary reader retains its original byte snapshot while settings publish atomically");
            Check(new DesktopPreferencesStore(path).Load().CaptureDelaySeconds == 4 && File.ReadAllBytes(path + ".bak").SequenceEqual(original),
                "Atomic settings publish advances the primary and records the exact previous primary as backup");
        }

        foreach (bool holdPrimaryToo in new[] { false, true })
        {
            // Direct native characterization on the supported Windows test
            // machine found that an open OLD backup blocks ReplaceFile even
            // with delete sharing. This is a bounded failure/preservation case,
            // not an assumption that the primitive supports that replacement.
            byte[] primaryBytes = File.ReadAllBytes(path), backupBytes = File.ReadAllBytes(path + ".bak");
            var updating = new DesktopPreferencesStore(path);
            DesktopPreferences draft = updating.Load();
            draft.CaptureDelaySeconds = holdPrimaryToo ? 6 : 5;
            Stopwatch clock = Stopwatch.StartNew();
            using (FileStream? primaryReader = holdPrimaryToo ? new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null)
            using (FileStream backupReader = new(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Expect<IOException>(() => updating.Save(draft), "An open old backup reports a bounded atomic-publication failure" + (holdPrimaryToo ? " with the primary also held" : ""));
                Check(ReadHeldStream(backupReader).SequenceEqual(backupBytes) &&
                    (primaryReader is null || ReadHeldStream(primaryReader).SequenceEqual(primaryBytes)),
                    "Failed publication keeps the exact held old-backup/primary reader snapshots");
                Check(File.ReadAllBytes(path).SequenceEqual(primaryBytes) && File.ReadAllBytes(path + ".bak").SequenceEqual(backupBytes) &&
                    draft.CaptureDelaySeconds == (holdPrimaryToo ? 6 : 5),
                    "An open old backup cannot cause loss of primary, backup or unsaved draft values");
            }
            clock.Stop();
            performance.Add(new { Operation = "Atomic settings save while old backup remains open with delete sharing",
                PrimaryAlsoHeld = holdPrimaryToo, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Scope = "Expected bounded IOException and preservation, based on separately recorded direct native contract; no non-atomic fallback." });
            Check(!Directory.GetFiles(folder, "sharing.json.*.tmp").Any() && !Directory.GetFiles(folder, "sharing.json.unreadable-*.json").Any(),
                "Open-backup failure cleans temporary output and does not invent corrupt data");
            updating.Save(draft);
            Check(new DesktopPreferencesStore(path).Load().CaptureDelaySeconds == draft.CaptureDelaySeconds,
                "Closing the old backup permits a successful save of the retained draft");
        }

        foreach (bool lockBackup in new[] { false, true })
        {
            string target = lockBackup ? path + ".bak" : path;
            var updating = new DesktopPreferencesStore(path);
            DesktopPreferences draft = updating.Load();
            draft.CaptureDelaySeconds = lockBackup ? 6 : 5;
            using var release = new ManualResetEventSlim(false);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task holdingReader = Task.Run(() =>
            {
                try
                {
                    using FileStream held = new(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    ready.TrySetResult(true);
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The transient sharing fixture was not released.");
                    Thread.Sleep(80);
                }
                catch (Exception error) { ready.TrySetException(error); throw; }
            });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                release.Set();
                updating.Save(draft);
            }
            finally { release.Set(); await holdingReader.WaitAsync(TimeSpan.FromSeconds(5)); }
            clock.Stop();
            performance.Add(new { Operation = "Atomic settings save with transient deny-delete reader",
                HeldPath = lockBackup ? "Backup" : "Primary", ReaderHoldMilliseconds = 80, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Scope = "Controlled local sharing conflict and release; not evidence of which external reader caused the historical error." });
            Check(new DesktopPreferencesStore(path).Load().CaptureDelaySeconds == draft.CaptureDelaySeconds,
                "Settings publish succeeds after a transient deny-delete " + (lockBackup ? "backup" : "primary") + " reader releases");
        }

        foreach (bool lockBackup in new[] { false, true })
        {
            string target = lockBackup ? path + ".bak" : path;
            byte[] primaryBytes = File.ReadAllBytes(path), backupBytes = File.ReadAllBytes(path + ".bak");
            var updating = new DesktopPreferencesStore(path);
            DesktopPreferences draft = updating.Load();
            string requestedTheme = draft.Theme == "Light" ? "Dark" : "Light";
            draft.Theme = requestedTheme;
            Stopwatch clock = Stopwatch.StartNew();
            using (FileStream held = new(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Expect<IOException>(() => updating.Save(draft), "Persistent deny-delete " + (lockBackup ? "backup" : "primary") + " reader reports a bounded publication failure");
            clock.Stop();
            performance.Add(new { Operation = "Atomic settings save with persistent deny-delete reader",
                HeldPath = lockBackup ? "Backup" : "Primary", ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Scope = "Expected local file-sharing failure; no timing pass threshold." });
            Check(File.ReadAllBytes(path).SequenceEqual(primaryBytes) && File.ReadAllBytes(path + ".bak").SequenceEqual(backupBytes) && draft.Theme == requestedTheme,
                "Persistent sharing failure preserves primary, prior backup and the unsaved settings draft");
            Check(!Directory.GetFiles(folder, "sharing.json.*.tmp").Any() && !Directory.GetFiles(folder, "sharing.json.unreadable-*.json").Any(),
                "Sharing failures clean their temporary file without mislabeling readable settings as corrupt");
            updating.Save(draft);
            Check(new DesktopPreferencesStore(path).Load().Theme == requestedTheme, "Settings retry succeeds after the persistent sharing fixture is closed");
        }
    }

    private static byte[] ReadHeldStream(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task CheckSettingsConcurrentRepetitionAsync(string folder)
    {
        // A real run failed in the original sequential recent-list bound loop.
        // Increase this related workload in a bounded way and retain every
        // observed failure; repetition does not establish its external cause.
        const int writerCount = 8, writesPerWriter = 8, readerCount = 2, readsPerReader = 64;
        string path = Path.Combine(folder, "repeated-sharing.json");
        var seed = new DesktopPreferencesStore(path);
        DesktopPreferences initial = seed.Load(); initial.Theme = "Dark"; seed.Save(initial);
        string[] expected = Enumerable.Range(0, writerCount).Select(index => Path.Combine(folder, $"repeat-{index:0000}_Aa.pdf")).ToArray();
        byte[] original = File.ReadAllBytes(path);
        using (FileStream onePublicationReader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            seed.AddRecent(expected[0]);
            Check(ReadHeldStream(onePublicationReader).SequenceEqual(original), "Held primary snapshot remains stable across one publication before the 64-write workload");
        }
        // The original primary is now the old backup. Close its handle before
        // another publication, matching the directly observed native contract.
        seed.ClearRecent();
        var errors = new ConcurrentQueue<string>();
        using var readersReady = new CountdownEvent(readerCount);
        using var start = new ManualResetEventSlim(false);
        int completedWrites = 0, completedReads = 0, writerFailures = 0, readerFailures = 0;
        Task[] readers = Enumerable.Range(0, readerCount).Select(reader => Task.Factory.StartNew(() =>
        {
            bool announced = false;
            try
            {
                readersReady.Signal(); announced = true;
                if (!start.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Concurrent settings readers were not started.");
                for (int index = 0; index < readsPerReader; index++)
                {
                    try
                    {
                        var view = new DesktopPreferencesStore(path);
                        DesktopPreferences prefs = view.Load();
                        IReadOnlyList<string> recent = view.RecentPaths;
                        if (view.LoadWarning is not null || prefs.Theme != "Dark" || recent.Count > writerCount || recent.Any(value => !expected.Contains(value, StringComparer.Ordinal)))
                            throw new InvalidOperationException("A concurrent reader observed defaults, invalid history or a recovery warning.");
                        Interlocked.Increment(ref completedReads);
                    }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref readerFailures);
                        errors.Enqueue($"Reader {reader}, sample {index}: {error.GetType().Name} {error.HResult:X8} {error.Message}");
                    }
                    Thread.Sleep(2);
                }
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref readerFailures);
                errors.Enqueue($"Reader {reader} lifecycle: {error.GetType().Name} {error.HResult:X8} {error.Message}");
            }
            finally { if (!announced) readersReady.Signal(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        if (!readersReady.Wait(TimeSpan.FromSeconds(5)))
        {
            start.Set();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));
            throw new TimeoutException("Settings sharing readers did not become ready.");
        }
        Stopwatch clock = Stopwatch.StartNew();
        Task[] writers = Enumerable.Range(0, writerCount).Select(writer => Task.Run(() =>
        {
            var store = new DesktopPreferencesStore(path);
            if (!start.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Concurrent settings writers were not started.");
            for (int index = 0; index < writesPerWriter; index++)
            {
                try { store.AddRecent(expected[writer]); Interlocked.Increment(ref completedWrites); }
                catch (Exception error)
                {
                    Interlocked.Increment(ref writerFailures);
                    errors.Enqueue($"Writer {writer}, operation {index}: {error.GetType().Name} {error.HResult:X8} {error.Message}");
                }
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();
        performance.Add(new { Operation = "Repeated atomic recent writes with concurrent settings readers",
            WriterCount = writerCount, WritesPerWriter = writesPerWriter, AttemptedWrites = writerCount * writesPerWriter,
            CompletedWrites = completedWrites, WriterFailures = writerFailures, ReaderCount = readerCount, AttemptedReads = readerCount * readsPerReader,
            CompletedReads = completedReads, ReaderFailures = readerFailures,
            ReaderLifetime = "Each sample constructs a fresh public DesktopPreferencesStore, invokes Load and RecentPaths, and validates its observed settings. No raw file oracle or old backup remains deliberately held across repeated publications.",
            ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, Failures = errors.ToArray(),
            Scope = "Bounded regression workload after an observed real ReplaceFile failure; no claim that repetition proves absence of future sharing conflicts." });
        Check(writerFailures == 0 && completedWrites == writerCount * writesPerWriter, "All 64 public AddRecent writes complete without hidden write failures");
        Check(readerFailures == 0 && completedReads == readerCount * readsPerReader,
            "All 128 fresh public Load/RecentPaths samples observe valid settings without read failures or false recovery/defaults");
        var final = new DesktopPreferencesStore(path);
        Check(final.Load().Theme == "Dark" && final.RecentPaths.Count == expected.Length && new HashSet<string>(final.RecentPaths, StringComparer.Ordinal).SetEquals(expected),
            "Repeated multi-store writes preserve all unique recent paths and unrelated preferences");
    }
}
