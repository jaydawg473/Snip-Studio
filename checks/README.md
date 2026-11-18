# Verification sources

These checks use synthetic data. Private screenshot examples, generated reports,
test executables, and signing credentials are excluded from this repository.

The 0.6.23 checks cover document history, failed replacement, native text zoom,
formatted-paste boundaries, OCR geometry and ordering, recorder notifications,
metadata waits, save ownership, and compact window layouts. Some use unshown
native controls. The save workflow check's full mode briefly displays its own
synthetic progress dialog; `--no-dialog` covers only file/state boundaries.

`build.ps1` compiles the application only. Its former `-Check` path is disabled
because that path launched unsigned programs. Compile the relevant C# fixtures
against the intended application assembly, then sign both application and check
executables with a valid timestamped certificate. Verify each exact file's
signature, timestamp, and SHA256 before execution. Use a fresh output directory
for every run and preserve failed attempts. Do not execute an unsigned fixture.

Reflection fixtures must run against the assembly version they target; newer
members and native control contracts may require updated fixture source. Helpers
using Windows Runtime metadata also need the corresponding installed Windows SDK
metadata references. PowerShell library checks use Windows PowerShell 5 in STA
mode unless their own header states otherwise. Read each fixture's argument and
side-effect contract before running it.

The sources alone do not demonstrate a passing run. `VALIDATION.txt` records the
signed 0.6.23 candidate's accepted checks and limits. Live capture, audio,
physical DPI/monitor behavior, clipboard/printing, and installed upgrade or
uninstall acceptance remain separate verification work.
