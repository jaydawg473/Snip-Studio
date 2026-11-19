# WinUI preview verification

Updated November 19, 2026 for `0.7.0-preview.2`, application file version `0.7.0.2`. **Production release status: INSUFFICIENT EVIDENCE.** The source workflows are implemented, but required native/device acceptance remains unavailable. A verified source check or signed package does not establish that acceptance.

## Current component results

The signed run completed November 19 at 17:42 UTC against source freeze `6C33D7561681E844A21DAE1427FCCADC67122599DD19B165C658483DD5CF326E`. Its execution receipt verified valid timestamped signatures on all executed tools and own/dependency assemblies. **1,673 unique component assertions passed**:

| Suite | Result | Exercised scope |
| --- | --- | --- |
| Backend | 77 PASS | Public session APIs on an STA context; image/history ownership, failed replacement preservation, atomic save/retry/cancellation, image formats and independent PDF image decoding. |
| Viewport | 1,335 PASS | Linked production geometry, ten groups including 60 Fit/DPI/aspect combinations; coordinate round trips, Fit, anchors, pan bounds, clipping and invalid/tiny layouts. No native WinUI pointer or physical-monitor checks. |
| Migration | 261 PASS | Editing pixels/history, exact text/style values, shapes/emoji/eraser transactions, move/resize/crop/removal, stale snapshots, OCR DTO/selection ownership, hidden-STA lifecycle, line/arrow endpoints, pre-canceled capture rejection, video-copy transactions, settings/recovery and controlled large-image measurements. |

The settings regression retained eight writers × eight writes and two readers × 64 samples. **64/64 public writes and 128/128 actual `DesktopPreferencesStore.Load`/`RecentPaths` samples passed with zero errors**, valid settings and all unique recent paths retained. These are real public store calls, not raw file readers. The workload and assertions were not weakened. The isolated recovery group's 20 assertions overlap this suite and are not added again. Earlier partial/failed runs and historical stable-app counts are also excluded.

At 17:43:25 UTC, two separately launched signed/preverified worker processes with distinct PIDs passed stale-snapshot settings merge and recent-path persistence checks without failures. This is controlled local cross-process evidence, not a native Settings-dialog/restart check. Its preparation reran 261 migration assertions; that repetition is not added to the component total.

Nine structural assertions for the signed manifest task and a separate competing-task registration-priority fixture passed against the same source freeze. They are separate build-tool evidence and are not added to the 1,673 workflow assertions.

## Additional adapter checks

The following signed runs completed at 17:33 UTC against prior freeze `2A49E0B2D37C040102080D51E97DF31761F9EEEB10A5E4F6F487966ED92F6C6A`. Comparing the freezes shows only settings production/test C# changed afterward; the tested media, backend and translation source is unchanged. These results are reported separately from the component total.

| Check | Result and limits |
| --- | --- |
| Offline translation | 7 PASS through the actual local adapter and signature/hash/lease gate. One ten-segment German-to-English batch preserved protected identifiers, leading-zero quantities, duplicates, blank and whitespace rows. Overall 1,220.35 ms, including 953.00 ms for translation. No configured server, other direction, semantic-quality certification, traffic capture, child peak memory or native dialog acceptance. |
| Generated video | 47 PASS for actual MP4 metadata, frame decoding/orientation, byte-exact copy, non-keyframe trim, export and failure/cancellation preservation. Generated input: two seconds, 640 × 360, 15 fps, painted markers and synthetic tone. Overall 1,072.62 ms. Audio presence was metadata-checked, not independently audio-decoded. No hardware recording or native WinUI playback. |

The 64,190-byte compact MP4 was rejected by the smaller-export budget rule without replacing the destination. Positive export used an explicitly labeled owned copy with an artificial 512 KiB MP4 free box. A smaller padded result does not prove improved compression of the compact original. Opaque non-video copy fixtures in the component suite establish transaction behavior only; they are distinct from the generated-media codec tests.

## Controlled image measurements

The current passing migration run used an opaque 4096 × 2160 PNG with a 16 × 12 colored-panel grid, 175,789 input bytes. These are backend operations within the check process, not application startup or native UI latency.

| Operation | Actual measurement |
| --- | --- |
| Open image bytes | 21.37 ms |
| First preview | 79.44 ms |
| Add stroke and render preview | Eight iterations; mean 83.18 ms, range 77.28–89.58 ms |
| Atomic PNG save | 213.51 ms |

Whole-process working-set samples were 107.60 MB before the session, 179.65 MB after save/independent decode and 108.70 MB after disposal without forced collection. Final private memory was 47.91 MB and managed heap 1.32 MB. MB means 1,000,000 bytes. These point samples include runtime, prior checks and transient allocations; they establish neither peak usage nor absence of a leak. Different runs are not a controlled optimization comparison. No speedup is claimed. Startup, real scrolling/capture/recording CPU, dropped frames and long-running memory remain unmeasured.

## Diagnoses, counterevidence and OCR limits

- **Settings public-read race fixed:** public readers did not acquire the existing cross-process writer mutex before initial load, and a controlled run observed six sharing errors. All five public disk paths now acquire the same bounded two-second mutex before loading. Backup recovery rereads current data under that lease; transient publication conflicts retain a bounded 310 ms retry without a delete/move fallback. The unchanged 64-write/128-read regression now passes.
- **Held-backup contract corrected:** eight direct native cases established that an open backup handle blocks `File.Replace` even with delete sharing, while primary-only/no-held-handle cases succeed. Failed calls preserved source, primary and backup bytes; release/retry succeeded. An earlier test premise expecting success while holding the old backup was invalid. The final tests preserve this expected-failure case, independently of the real public-read race.
- **Text-cache diagnosis retracted:** source review found thread-local storage and locking; public rich-text editing/history survived another STA's bounded hidden message-loop startup and exit. No global-disposal bug was established or patched.
- **Supplied OCR accuracy remains limited:** real Windows OCR returned all six rows from one 301 × 169 filename snip, but **0/6 exact strings matched**. Selection indices, verbatim conversion order, Undo and Redo passed. The unchanged-pixel probe had 47 ordinal edits over 271 reference characters (17.34%); none of 16 alternative pixel preparations improved it. Signed stable 0.6.24 produced the same six native strings. The production recognizer is unchanged; no guessed identifiers or reference-driven correction were introduced. A second supplied image lacks an exact transcription, so no exact-accuracy score is claimed for it.

The earlier supplied-snippet run measured 659.68 ms for recognition and 5,668.56 ms for source-style conversion, one sample rather than a general latency guarantee. OCR review now labels strings unverified, says conversion keeps them verbatim and defaults to Close. Font recovery remains approximate. These safeguards do not repair recognition accuracy; native text activation, visual matching and drag selection remain unverified. Supplied filenames and images are kept out of these public documents.

## Required native, device and installation evidence

The latest available desktop observation on November 19 at 12:39 local time was still the Windows lock screen. No inputs were performed. The requested alternate computer is not exposed to the current host. An older preview.1 resource-repair probe produced a window/accessibility tree behind the lock screen; it established neither rendering nor interaction and is not a launch of the current candidate.

| Gate | Current evidence boundary |
| --- | --- |
| Current application launch/rendering; pointer, keyboard, text scale and native pickers | NOT TESTED on an accessible unlocked desktop. |
| Supplied OCR examples, icon preservation, visual text/font/color/size/position, Text activation and native drag/copy/edit | Controlled OCR accuracy failure and ownership/history subsets recorded above; native acceptance NOT TESTED. |
| Editing, failed-save recovery, clipboard, Share, Print and external-editor round trip through the actual UI | Component subsets PASS; native workflows NOT TESTED. |
| Enlarge/shrink, excess canvas, contextual tools, Fit/manual pan, high DPI and multiple monitors | Geometry model PASS; rendered/native/physical acceptance NOT TESTED. |
| Live rectangle/freeform/window/monitor/HDR/scrolling capture, cancellation and partial-result review | Input-validation subsets PASS; actual capture pixels/controllers NOT TESTED. |
| Actual recording/audio devices, boundaries, Stop/recovery and native video playback/review | Generated-media adapters PASS separately; device/native acceptance NOT TESTED. Recording Pause/Resume is unavailable. |
| Preferences/restart, recent paths on real storage, tray/startup/global shortcuts, Help, release metadata and configured translation server | Controlled settings/local-translation checks PASS; Windows Shell/network/native/service acceptance NOT TESTED. |
| Install, upgrade/uninstall and other-PC compatibility | NOT TESTED. No installation or public GitHub Release is established by these checks. |

## Build, signing and package evidence

A clean modern .NET/WinUI Release build completed at 17:45 UTC against the same source freeze with **zero warnings and zero errors**. The compiler reported 18.44 seconds; this is build duration, not application startup. The build receipt records no application launch or installer execution. Earlier publication verified application-resource closure, but the clean build alone does not verify the final published installer or portable archive. A delivered candidate's included `VALIDATION.txt`, `SOURCE`, `SIGNATURES` and hash manifest must identify the exact package bytes, source binding, verified executable closure, compiled PRI/XAML, notices and audit outcomes. Package-specific receipts are the authority for that artifact; this source document does not predeclare their success.

Every executable run or delivered must have a valid timestamped signature before execution. Our outputs use Automation Forge; third-party binaries retain valid original publisher signatures. Package signatures/strong names do not replace Authenticode. No unsigned fallback, security-product bypass or disabled protection is acceptable. A NuGet transitive metadata query returned no reported known vulnerabilities; that is current package-source metadata, not an independent security certification.

Failed reports remain preserved for diagnosis. Component, native visual/input, hardware, packaging, installation and publication are distinct gates. Even a technically verified signed candidate remains **INSUFFICIENT EVIDENCE** for a production release while required native/device acceptance is unavailable.
