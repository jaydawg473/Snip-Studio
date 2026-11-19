# Windows OCR preparation probes

This diagnostic compares native pixels, padded recognition copies, integer scaling, ClearType channel neutralization, and raster-derived row isolation. It never edits the document, substitutes characters, or picks a production result using an expected identifier. The public source contains no supplied filenames or fixture paths.

An external private JSON file supplies `Fixtures`, each with `Name`, `InputPath`, and optional `ExpectedLines`. Expected lines are used only to report ordinal comparison counts in private evidence. Every variant retains its uncorrected raw text, word coordinates mapped to source pixels, prepared PNGs, input/preparation hashes and elapsed time. These are diagnostic measurements, not assertions of OCR correctness.

Root must build with the audited SDK and sign/verify every owned executable and dependency before execution. No unsigned build is launched by this project. After signature verification, the parameterized entry point is:

```text
SnipStudio.Ocr.Probes.exe --fixtures <private-manifest.json> --output <new-empty-evidence-folder> --set quick
```

`quick` runs 12 variants; `all` runs 17. Each native operation has a ten-second cancellation budget and a twelve-second bounded wait. A native timeout stops the entire probe run rather than overlapping another request. The run has a 120-second budget and accepts at most eight fixtures. Raster row detection is an experimental dark-text diagnostic; it is not used in production and is not a general text/graphic classifier. Native interaction, clipboard and hardware capture are outside this diagnostic's scope.
