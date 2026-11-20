# WinUI preview verification

Updated November 20, 2026 for `0.7.0-preview.4`, application file version `0.7.0.4`. **Production release status: INSUFFICIENT EVIDENCE.** The Snip-style recording route now has a new clean capture-bar layout in source. Its build, native and package checks are pending; previous large-window/provisional results are not acceptance of this layout.

## Preview.4 current gates

| Change | Evidence and remaining check |
| --- | --- |
| Compact native capture row | Source aligns controls and includes capture-menu icons. Render, clipping, focus and menu access await the signed current candidate. |
| Minimum sizes | Source requests launcher 520 × 260, image editor 520 × 320, clean recording bar 460 × 128 and video review 640 × 480 DIP, scaled and work-area clamped. A capture warning raises the height floor to 260 DIP with a details body bounded at 96 DIP. Final native size tracking and DPI behavior are NOT TESTED. |
| Settings | Source temporarily enlarges the host toward 680 × 640 DIP, bounds the scrollable dialog, then restores prior bounds unless the user resized; manual movement retains its new position. Native Save/Cancel, scrolling and restore/move/resize behavior are NOT TESTED for preview.4. |
| Clean recording bar | Source shows inline Start or Stop, timer, microphone/system-audio boxes, Options, small selected-area/status rows and native caption close. It hides the capture heading/instructions/Open video/full setup. One left-aligned advanced form opens in a flyout without increasing the parent height. This replacement layout is NOT native-verified. |
| Recording route | Video mode → New/Ctrl+N → Rectangle/Window selection; cancel keeps the image/draft and opens no recorder. Start alone prepares the configured-folder MP4 and minimizes; taskbar restore exposes Stop, then review. The user accepted the previous controls' functionality but rejected the large form; this is not a controlled test of the new bar, audio or recording hardware. |
| Folder preparation | Source allows one request per recording window with an eight-second await, observes late failure and blocks a duplicate request while it remains pending. This is not proof of provider cancellation or hardware recording. |
| Current build, signed tests and package | Pending for the clean bar. The prior D51 routing candidate passed 1,673 components, 7 local translation and 47 generated-video assertions plus static closure D721; those results now apply to its old visual candidate, not this replacement. |

Provisional reference: source freeze `D51C2C5AD4145F8C7F7DA6A88E5612719FC160880F7734815A6001855EBF4957`, static closure SHA256 `D721F21A4B5EC390EEC314F7A65F2D8B1A4E7125C5000AF5C0574FD6E3A07D57`. Earlier Settings/minimum observations remain reference-only. No final clean-bar archive, installation, Start/Stop/audio or device acceptance is claimed.

No new OCR accuracy or performance result is claimed. The hybrid WinUI/WinForms/GDI+ boundary and signing requirements remain unchanged. Real capture/recording/audio, physical DPI/monitors and installation still require separate evidence.

## Preview.3 historical gates

The following preview.3 results apply to their exact earlier source and artifacts. They do not verify preview.4 minimums, Settings behavior or capture-row rendering.

| Gate | Result |
| --- | --- |
| Clean build and publication | PASS: code commit `7af89becf5092265ec65ccd208c175eaa5da7275`, zero warnings/errors. |
| Component checks | PASS: 77 backend + 1,335 viewport + 261 migration = 1,673 assertions, rerun at 21:55 UTC against the final signed backend. Repeated runs are not added. |
| Local translation | PASS: 7 assertions using the local German-to-English model, preserving protected identifiers, quantities, duplicate and blank rows. No cloud-provider or packet-traffic acceptance. |
| Generated video | PASS: 47 assertions for metadata, orientation/decoded frames, exact copy, trim, export and failure/cancellation preservation. AAC presence is metadata-only. Positive smaller export uses a padded fixture; it does not prove better compression of the compact original. No hardware recording or audible playback acceptance. |
| Static packages | PASS: 745 exact payload paths/hashes; 358 signed EXE/DLL files plus other PE metadata checks; preserved vendor bytes; valid timestamped Setup and complete embedded uninstaller signatures; installer extraction and ZIP entries match the frozen payload. |
| Native reference layout | PARTIAL: signed A02 reference showed compact 54/46-DIP rows, an uncut New button, no extra capture overflow, a smaller centered image window, readable Text options and a working 125% preset. No precise outer-window measurement is claimed. |
| Remaining native checks | Final smaller-launcher/options-reserve fixes, 100%/shortcut routing, manual-size retention, compact overflow, capture/scrolling, recording/audio and physical DPI/monitors remain unverified. The user confirmed active desktop use, so further desktop input was paused. |
| Installation/public release | Pending approval. Setup and Uninstall were never executed; no public GitHub Release was published. |

Final source freeze: `10FC38988A6903B8124E28B847B75EE6363101A7B8AD3DBC243B3317FBEBE1BC` (467 compile/resource/config inputs). Final signed backend: `2106140A7D84456F120C0F00BC7E672A8D71B1F6B78D0C4805BCC5F6263B48A3`. Artifacts embed code commit `7af89be`; subsequent documentation-only commits preserve these build inputs. Local delivery is under `artifacts/release-0.7.0-preview.3`.

Static package receipt SHA256: `91A5B9FA8D9967DE5B911AF42C01F24BC8C71C6B9FA2EB5DD36A1B8A80AA252C`. Signed Setup SHA256: `94C232681AF4B519F824375B47B29E68958116B9A60889914CA339EC482826D2`; portable ZIP SHA256: `F2165EA9B2F274F3AC1AA621F6AD79848530C849E5648BF265326F8646A0AD8C`.

Native reference evidence binds earlier freeze `A02F23EBD50CDBA68AA723CFCBE67A44CBFD813B4F1E445FE40CD80F41E56D43`. It used a byte-identical renamed signed host with matching PRI for desktop discovery; the canonical shipping host remains unchanged. Later 100%/Ctrl+0 attempts were inconclusive during desktop focus changes, not proof of an application regression. That test exposed a clipped launcher hint and Fit changing from 100% to 76.3% when Text options opened. The final source reduces the empty layout and reserves 40 DIP for options; these repairs are not claimed native-verified.

No OCR accuracy or performance improvement is inferred from the layout change. The earlier dense six-row example retained 0/6 exact identifiers, matching stable output. Raw extraction and user values remain reviewable without guessed corrections. Earlier results below remain version-scoped historical evidence.

## Preview.2 historical component results

All evidence from this section onward describes preview.2 candidates and source freezes. It is retained for traceability and does not verify the restored preview.3 layout.

The signed run completed November 19 at 17:42 UTC against source freeze `6C33D7561681E844A21DAE1427FCCADC67122599DD19B165C658483DD5CF326E`. Its execution receipt verified valid timestamped signatures on all executed tools and own/dependency assemblies. **1,673 unique component assertions passed**:

| Suite | Result | Exercised scope |
| --- | --- | --- |
| Backend | 77 PASS | Public session APIs on an STA context; image/history ownership, failed replacement preservation, atomic save/retry/cancellation, image formats and independent PDF image decoding. |
| Viewport | 1,335 PASS | Linked production geometry, ten groups including 60 Fit/DPI/aspect combinations; coordinate round trips, Fit, anchors, pan bounds, clipping and invalid/tiny layouts. No native WinUI pointer or physical-monitor checks. |
| Migration | 261 PASS | Editing pixels/history, exact text/style values, shapes/emoji/eraser transactions, move/resize/crop/removal, stale snapshots, OCR DTO/selection ownership, hidden-STA lifecycle, line/arrow endpoints, pre-canceled capture rejection, video-copy transactions, settings/recovery and controlled large-image measurements. |

The settings regression retained eight writers × eight writes and two readers × 64 samples. **64/64 public writes and 128/128 actual `DesktopPreferencesStore.Load`/`RecentPaths` samples passed with zero errors**, valid settings and all unique recent paths retained. These are real public store calls, not raw file readers. The workload and assertions were not weakened. The isolated recovery group's 20 assertions overlap this suite and are not added again. Earlier partial/failed runs and historical stable-app counts are also excluded.

At 17:43:25 UTC, two separately launched signed/preverified worker processes with distinct PIDs passed stale-snapshot settings merge and recent-path persistence checks without failures. This is controlled local cross-process evidence, not a native Settings-dialog/restart check. Its preparation reran 261 migration assertions; that repetition is not added to the component total.

Nine structural assertions for the signed manifest task and a separate competing-task registration-priority fixture passed against the same source freeze. They are separate build-tool evidence and are not added to the 1,673 workflow assertions.

## Preview.2 historical adapter checks

The following signed runs completed at 17:33 UTC against prior freeze `2A49E0B2D37C040102080D51E97DF31761F9EEEB10A5E4F6F487966ED92F6C6A`. Comparing the freezes shows only settings production/test C# changed afterward; the tested media, backend and translation source is unchanged. These results are reported separately from the component total.

| Check | Result and limits |
| --- | --- |
| Offline translation | 7 PASS through the actual local adapter and signature/hash/lease gate. One ten-segment German-to-English batch preserved protected identifiers, leading-zero quantities, duplicates, blank and whitespace rows. Overall 1,220.35 ms, including 953.00 ms for translation. No configured server, other direction, semantic-quality certification, traffic capture, child peak memory or native dialog acceptance. |
| Generated video | 47 PASS for actual MP4 metadata, frame decoding/orientation, byte-exact copy, non-keyframe trim, export and failure/cancellation preservation. Generated input: two seconds, 640 × 360, 15 fps, painted markers and synthetic tone. Overall 1,072.62 ms. Audio presence was metadata-checked, not independently audio-decoded. No hardware recording or native WinUI playback. |

The 64,190-byte compact MP4 was rejected by the smaller-export budget rule without replacing the destination. Positive export used an explicitly labeled owned copy with an artificial 512 KiB MP4 free box. A smaller padded result does not prove improved compression of the compact original. Opaque non-video copy fixtures in the component suite establish transaction behavior only; they are distinct from the generated-media codec tests.

## Preview.2 historical image measurements

The preview.2 passing migration run used an opaque 4096 × 2160 PNG with a 16 × 12 colored-panel grid, 175,789 input bytes. These are backend operations within the check process, not application startup or native UI latency.

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

The earlier supplied-snippet run measured 659.68 ms for recognition and 5,668.56 ms for source-style conversion, one sample rather than a general latency guarantee. OCR review labels strings unverified, says conversion keeps them verbatim and defaults to Close. Font recovery remains approximate. Later native observation confirmed Text activation and in-editor character drag-selection on one candidate; it did not establish exact source font/color/position recovery or correct the character-accuracy limit. Supplied filenames, typed test strings and images are kept out of these public documents.

## Preview.2 partial native evidence and remaining gates

An accessible desktop became available after the earlier locked-screen observations. The private native receipt recorded at 18:34 UTC binds signed candidate executable `B11E3E49328536304AE1CDFDC061B0AD976D01726F6A0CD36B237CA2DCF92AA9`, source commit `1b92653d680ab52580361156d7d7d586b733499a`. It records a rendered frontend opening the supplied 301 × 169 image, six-row OCR review, conversion selecting Text, RichEditBox character drag-selection, Ctrl+A and an exactly retained typed replacement on Apply. Undo/Redo were visually observed before later unattributed input made subsequent state changes unsuitable as agent evidence.

The native system Size workflow reduced the window from 1086 × 754 through 713 × 753 to 713 × 493; toolbar/status overflow and wrapping remained usable. A native Save As dialog created a new 301 × 169 PNG, 7,761 bytes, with Saved feedback and the dirty marker removed. The supplied input remained unchanged. These observations cover one sequence, not all annotation/history or save-failure behavior.

Ctrl+O, Ctrl+S, Ctrl+0 and F11 on the older instance after picker/system-sizing interactions were inconclusive; application cause versus focus/tool routing is unresolved. On a fresh instance, F11 entered 1920 × 1080 full screen and restored the window before dialogs. This scoped pass does not establish the old post-dialog behavior or a general keyboard pass; a global shortcut-registration failure is not established.

The earlier sequence exposed a clipped conversion label and excessive recovered-font decimals. Source freeze `71948BDA4063784C15B30653F101913D57F232D700373F7688698DE30C7992E7` shortens the action to Convert text and adds a precision-preserving two-decimal display formatter. Build/publish passed with zero warnings/errors. Native retesting at 18:49 UTC bound frontend SHA256 `00154685D0DDE7596227DCCBA41E5C0BFFB89421DAF9F2ED0274B81A61FFD930` and confirmed the complete Convert text label visible and conversion selecting Text. For a recovered `NumberBox.Value` of `11.333333015441895`, displayed text was `11.33`; leaving the field with focus/Tab retained the exact underlying `11.333333015441895`. Explicitly entering `12.5` and tabbing committed `12.5`. These UX checks pass on the updated frontend; the broader native sequence above was on its predecessor. Backend, viewport and settings source is unchanged by the three frontend edits, so existing component evidence retains that limited scope.

| Gate | Preview.2 evidence boundary |
| --- | --- |
| Application rendering, native input and file pickers | PARTIAL: Open, text drag/type/Apply, Save As and fresh-instance F11 full-screen/restore observed. Post-dialog shortcut routing, text scaling and broader input/pickers remain unverified. |
| Supplied OCR examples, icon preservation, visual text/font/color/size/position, Text activation and native drag/copy/edit | PARTIAL: six-row review, visible conversion action, Text activation, in-editor selection/edit and exact size-display round trips observed; exact OCR strings failed as above. Visual source matching and OCR-band copy remain unverified. |
| Editing, failed-save recovery, clipboard, Share, Print and external-editor round trip through the actual UI | PARTIAL: text Apply/Undo/Redo and one PNG Save As observed. Other tools, failed-save recovery and external integrations remain NOT TESTED. |
| Enlarge/shrink, excess canvas, contextual tools, Fit/manual pan, high DPI and multiple monitors | PARTIAL: one reduction to 713 × 493 with usable overflow, plus fresh-instance F11 full-screen/restore observed; Fit/manual zoom, general contextual cases and physical DPI/monitors remain unverified. |
| Live rectangle/freeform/window/monitor/HDR/scrolling capture, cancellation and partial-result review | Input-validation subsets PASS; actual capture pixels/controllers NOT TESTED. |
| Actual recording/audio devices, boundaries, Stop/recovery and native video playback/review | Generated-media adapters PASS separately; device/native acceptance NOT TESTED. Recording Pause/Resume is unavailable. |
| Preferences/restart, recent paths on real storage, tray/startup/global shortcuts, Help, release metadata and configured translation server | Controlled settings/local-translation checks PASS; Windows Shell/network/native/service acceptance NOT TESTED. |
| Install, upgrade/uninstall and other-PC compatibility | NOT TESTED. No installation or public GitHub Release is established by these checks. |

## Build, signing and package evidence boundaries

A clean modern .NET/WinUI Release build completed at 17:45 UTC against the same source freeze with **zero warnings and zero errors**. The compiler reported 18.44 seconds; this is build duration, not application startup. The build receipt records no application launch or installer execution. Earlier publication verified application-resource closure, but the clean build alone does not verify the final published installer or portable archive. A delivered candidate's included `VALIDATION.txt`, `SOURCE`, `SIGNATURES` and hash manifest must identify the exact package bytes, source binding, verified executable closure, compiled PRI/XAML, notices and audit outcomes. Package-specific receipts are the authority for that artifact; this source document does not predeclare their success.

Every executable run or delivered must have a valid timestamped signature before execution. Our outputs use Automation Forge; third-party binaries retain valid original publisher signatures. Package signatures/strong names do not replace Authenticode. No unsigned fallback, security-product bypass or disabled protection is acceptable. A NuGet transitive metadata query returned no reported known vulnerabilities; that is current package-source metadata, not an independent security certification.

Failed reports remain preserved for diagnosis. Component, native visual/input, hardware, packaging, installation and publication are distinct gates. Even with the recorded partial native sequence, the candidate remains **INSUFFICIENT EVIDENCE** for a production release while required acceptance is incomplete.
