# Migration backend checks

This console project exercises public migration-backend APIs on a controlled STA
context. It is a separate evidence group from the original backend and viewport
checks. Build output must be signed by Automation Forge and its complete executed
dependency closure verified before running. A build is not a test result.

Run the signed executable with `--output` and a new, empty evidence directory.
The process returns zero only after all checks pass and writes
`migration-checks.json`, a flattened crop export, and a controlled large-image
performance export. A failure retains its partial assertion count and exception;
do not add a partial failed count to a later successful total.

Checks cover exact source pixels around shapes and moves; crop geometry and
retained pixels; erase, delete, and history; text identifiers, duplicate lines,
user edits and chosen color/size; detached public annotation/OCR DTOs; cancellation
before OCR starts; failed mutation preservation; and flattened export/reopen.
It also checks stale and cross-session selections, continuous erasing between
sparse pointer samples as one Undo step, invalid/pre-canceled capture requests
that return before desktop access, and atomic byte-copy cancellation/failure/retry
with an explicitly opaque non-video file fixture. The copy fixture is never
passed to a decoder and does not establish video playback or codec correctness.
Combined geometry/style and emoji geometry/rotation must undo in one action.
Style-only edits of horizontal/vertical lines and partially cropped objects must
preserve their zero-span or out-of-image geometry.

The lifecycle group renders styled rich text on its owning STA, starts and exits
an independent STA's `Application.Run(ApplicationContext)` loop with a hidden
dispatcher control, then renders/adds rich text again and verifies exact
content/style/pixels/history. Completion waits for the loop's owned objects to
be disposed. Its five-second bound is an operation timeout, not a speed target.
This exercises public behavior without reflecting private caches, displaying
forms, capturing pixels from the desktop, or generating mouse/keyboard input.
The line/arrow group applies endpoints and style together and verifies one Undo
restores both.

Settings sharing fixtures examine held read/delete handles on the primary and
backup during publication. Direct native characterization on November 19, 2026
showed that a held primary permits one publication, while an open old backup
blocks it on the tested Windows/.NET runtime. The tests therefore verify a
single supported primary-reader publication, bounded failure with unchanged
primary/backup/draft for held old backups, and successful retry after release.
Controlled deny-delete handles test
transient release and persistent failure, preserved drafts/primary/backup,
temporary-file cleanup and later retry. A bounded workload performs 64 recent
writes across eight stores with two independent readers. Every sample constructs
a fresh public `DesktopPreferencesStore` and calls `Load` and `RecentPaths`;
there is no raw file-reading oracle. Writer and reader failures are recorded and
asserted separately, with every observed error retained. An old snapshot is held through one separate publication
and then closed before repeated writes. It records every observed error rather than retrying hidden failures in
the test. This regression was added after an actual atomic-replace failure;
passing repetition does not prove that future external sharing conflicts cannot
occur or identify which external reader caused the historical failure.

## Direct native file-replacement characterization

```text
SnipStudio.Migration.Checks --replace-baseline --output <new empty directory>
```

This separate opt-in mode bypasses the preferences store and makes a direct
`File.Replace` call in each of eight isolated cases: neither, primary-only,
backup-only, or both held read handles, each with read/delete or read/write/delete
sharing. It records exact bytes and hashes for source, primary and backup before
and after the call; held-reader bytes; exceptions/HRESULTs; directory entries;
and one direct retry after closing handles when source/destination remain.
It has no delete/move fallback. `CharacterizationComplete` means the observations
finished; native failures remain explicit and this is not a migration pass or an
assertion count.

The [Microsoft ReplaceFileW reference](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
(updated June 1, 2023; read November 19, 2026) describes replacement access and
error-dependent file names. [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0)
states that an existing backup is replaced. These descriptions do not establish
that an already-open old backup can always be replaced with delete sharing.
The recorded native run on Windows 10.0.26300.0/.NET 10.0.15 found neither-held
and primary-only cases succeeded in both sharing modes. Backup-only and both-held
cases returned `0x80070497`, retained source/primary/backup and held bytes, then
succeeded after the handles closed. This disproved the original combined-success
test expectation. The revised tests require preservation and bounded error while
the old backup is held, followed by successful save after release; no production
non-atomic fallback was introduced. Existing failed run evidence remains intact.

Newest-backup recovery also has an isolated mode:

```text
SnipStudio.Migration.Checks --settings-recovery --output <new empty directory>
```

It loads a stale settings draft, changes the backup, removes or corrupts the
primary, and checks that Save merges only the draft's changed field onto that
latest backup while preserving recent paths and unknown JSON. An exclusively
locked latest backup must fail without creating/overwriting a primary, changing
the backup/draft or producing false corruption artifacts; unlocking it permits
retry. This group also runs in the default suite. Its separate receipt isolates
diagnosis and must not be added again to the default assertion total.

The performance sample measures a 4096 × 2160 opaque colored-panel PNG grid,
eight edit-and-preview operations, and an atomic PNG save. Timings have no pass
threshold. Sampled memory belongs to the entire process and is not a peak-memory
or leak measurement. Report input characteristics and elapsed values when citing
the results; this fixture does not establish speed for all images.

These checks do not accept WinUI controls, native text editing or pointer input,
live OCR accuracy or selection/conversion, clipboard/pickers, capture/scrolling,
recording/audio/video, hardware DPI/monitors, installation, updates, or security
approval. Those workflows require separate signed native acceptance.

## Settings workers: separate cross-process evidence

The default suite links the exact pure frontend `DesktopPreferences.cs` and tests
stale per-field saves, concurrent independent stores, recent-path merging,
unknown JSON, backup recovery and failure preservation in isolated output files.
It also prepares a `settings-workers` directory. This preparation is not a
cross-process pass.

After verifying the signed test program and its complete executed dependency
closure, an external orchestrator must start two separate instances:

```text
SnipStudio.Migration.Checks --settings-worker <absolute theme.task.json path>
SnipStudio.Migration.Checks --settings-worker <absolute width.task.json path>
```

Wait for both ready markers, create both release markers, then await both worker
exit codes. Each worker captures its preferences before writing its ready file,
waits at most 15 seconds for release (task maximum 20 seconds), adds its recent
file, and saves its changed field from that stale baseline. The test never spawns
an executable itself.

Each task JSON contains absolute `SettingsPath`, `ReadyPath`, `ReleasePath`,
`ReceiptPath`, `RecentPath`, optional `Theme`/`StrokeWidth`, and
`WaitMilliseconds`. All paths must be distinct and in the task's isolated
directory. Markers and receipt must not already exist. Workers produce
`theme.receipt.json`/`width.receipt.json` containing process IDs, original loaded
preferences, observed saved values, pass/failure and timestamp.

Finally run the signed program with:

```text
SnipStudio.Migration.Checks --verify-settings-workers <absolute settings-workers directory>
```

It checks two distinct process IDs and original baselines, both saved fields,
both recent paths and unknown JSON, then writes a separate
`cross-process-checks.json`. Keep this result separate from deterministic
assertion totals. Do not overwrite old evidence or reuse released marker files.

## Opt-in real Windows OCR fixtures

This mode reads local images supplied in a private JSON manifest. It does not
capture the desktop or interact with native UI, and it can run while the desktop
is locked. Its executable and dependencies still require signature verification.

```text
SnipStudio.Migration.Checks --ocr-fixtures <absolute private manifest.json> --output <new empty directory>
```

The manifest schema is:

```json
{
  "Fixtures": [
    {
      "Name": "dense-filenames",
      "InputPath": "C:\\isolated-evidence\\dense-filenames.png",
      "ExpectedLines": ["00007_Aa.pdf", "00007_Aa.pdf"]
    }
  ]
}
```

Supply complete expected lines in order, including duplicates and meaningful
spaces. Comparisons are ordinal: no trimming, case folding, spelling correction,
identifier substitutions or deduplication. The report records raw and recovery
text, actual lines, exact missing/extra rows and order, source hashes/dimensions,
selection source indices, conversion annotations, rendered output, Undo/Redo
pixel preservation, real OCR/style timing and process memory samples. A mismatch
fails that fixture and remains visible in the evidence.

This mode produces `ocr-fixture-checks.json`, raw text files and converted PNGs.
Keep these supplied-image/recognized-text artifacts private. They are not public
test fixtures or release-package content. Its results are separate from the
deterministic assertion count. A native timeout stops subsequent fixtures;
the orchestrator must observe process completion before launching a replacement.
This mode does not verify WinUI Text-tool activation, pointer selection, visual
font accuracy, live clipboard, capture or other hardware workflows.

## Opt-in actual offline translation smoke

```text
SnipStudio.Migration.Checks --translation-smoke <absolute reviewed bundle directory> --output <new empty directory>
```

This mode calls the public `OfflineTranslationAdapter(bundlePath)`, discovers its
installed language pairs and runs one German-to-English batch through the real
local model. Node starts only through the adapter's existing signature/hash/lease
gate; this test does not spawn an alternate runtime or call a configured server.
Use the exact reviewed staged bundle with its original signed Node and pinned
code/models. The signed test assembly and executed dependencies must be verified
before running, as for every other mode.

Ten read-only source rows cover German prose, leading-zero/mixed-case file IDs,
repeated identifiers, an alphanumeric model code, duplicate rows, an empty row,
whitespace-only row and quantity `0007`. The checks require every source row and
exact literal occurrence/order to survive, blank/whitespace rows to remain exact,
and meaningful German prose to change into nonempty output. They do not prescribe
a guessed English translation. Source and observed output are retained unchanged
in `translation-smoke-checks.json`, including all row outcomes if a literal fails.

The complete language-discovery/model operation has an 85-second cancellation
budget; elapsed discovery/translation and parent-process memory are recorded
without speed thresholds. Results are separate from deterministic assertions.
A smoke pass establishes this local model workflow on these inputs; it does not
establish semantic translation quality, other model directions, packet-level
network isolation, child-process peak memory, or native WinUI acceptance.
