# Backend migration checks

These checks exercise the public WinUI backend adapter on an STA context: image ownership, exact source pixels, drawing/history, failed replacement preservation, dirty revisions, atomic save failure/retry, supported exports, cancellation, thread ownership and disposal while an encoder is active.

They do not launch WinUI controls or validate native pointer interaction, OCR, capture/audio, clipboard, installation, accessibility or physical DPI/monitor behavior.

Build through the reviewed migration build workflow. Before execution, sign and verify the checks executable and its managed application dependencies using the timestamped Automation Forge release process. Preserve valid original publisher signatures on runtime binaries. Do not use `dotnet run` or automatic test execution to bypass that gate.

Run the signed executable with `--output` followed by a new, empty evidence directory. It writes `backend-checks.json` and the generated image/PDF fixtures there. No existing evidence or user files are deleted.
