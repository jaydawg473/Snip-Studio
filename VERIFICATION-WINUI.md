# WinUI preview verification

November 18, 2026 local work session; some evidence timestamps are November 19 UTC. Version `0.7.0-preview.1`. This is a migration preview, not a replacement for the stable application.

| Check | Result and scope |
| --- | --- |
| Build | x64 modern .NET / WinUI build succeeded with zero warnings and zero errors. |
| Backend | 77 signed assertions passed on a controlled STA context. Image/history preservation, failed replacement, atomic save failure/retry/cancellation, ownership, formats and independent PDF image decoding were checked. |
| Viewport | 1,335 signed assertions passed across ten groups, including 60 Fit/DPI/aspect combinations. Physical pixel/DIP math, source round trips, Fit, manual anchors, pan bounds, clipping and invalid/tiny layouts were checked. These are model checks, not physical monitor tests. |
| Manifest build task | Nine structural assertions and a safe competing-task priority case passed. The task is a precompiled, signed copy of the pinned MIT source fragment. Build diagnostics positively identify it as the executing task. |
| Published XAML | The corrected publish automatically contains the application PRI plus App/MainWindow/EditorViewport XBF. All four hashes match build output. No manual resource copies are needed for the corrected published folder. |
| Native probe | An earlier signed resource-only repaired folder created the preview window and exposed its controls through accessibility. Executable bytes were unchanged from the failed launch. The desktop was locked, so application rendering and input workflows were not accepted. |
| Source review | Independent reviews corrected failed-open dirty state, long-stroke truncation, stale previews, clipboard UI waits, Fit intent, startup diagnostic scope and resource publication. |
| Installation / public release | Not performed. |

The failed viewport attempt, failed XML-query fixture, compile/path failures and initial startup crash are preserved separately; their partial assertions are excluded from the accepted totals. Repeated manifest executions are counted once. The old WinForms 0.6.24 totals are not WinUI results.

## Signing and privacy

The SDK/compiler/MSBuild and restored build/runtime tools were audited before execution. Unsigned application-output templates were treated as data and never run. Unused C++ WebView2 generator DLLs remain outside the selected C# tool closure. The vendor's temporary inline manifest task is overridden by the signed precompiled task, with no unsigned fallback.

The first portable executable closure contained 357 EXE/DLL files, with valid timestamps and signatures verified before launch: Automation Forge on the three application-owned files and original publishers on dependencies. Corrected final package checks are recorded separately; source compilation alone does not establish executable signatures. Debug PDBs are excluded from delivery because they contain development paths. Upstream notices are required in the delivered folder.

Startup diagnostics use the separate preview profile, redact profile/application paths, remain bounded below 64 KB and stop observing failures after successful activation. They do not handle or suppress the original exception. No screenshots, clipboard data, launch arguments or document contents are deliberately logged.

## Remaining acceptance

Native Open/Paste, drawing and pointer cancellation, Undo/Redo, Save/Discard/Cancel, failure recovery, keyboard focus/shortcuts, compact resizing, rendered theme/contrast/text-scale behavior and physical DPI/monitor changes remain unverified. The initial preview uses serialized full-image PNG encoding/decoding for committed edits. No startup, memory or end-to-end performance improvement is claimed.

Capture, scrolling capture, OCR/editable text, the remaining editing tools, recording/audio, recent files and Windows integration are subsequent migration stages. Do not interpret inherited source or passing model checks as those features working in WinUI.
