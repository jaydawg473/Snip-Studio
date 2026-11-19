# Snip Studio 0.7.0-preview.1

Automation Forge — WinUI 3 migration preview.

The first migration introduces a native WinUI window and viewport around the existing document engine. Source implements Open/Paste, Pen/Highlighter/Rectangle, Undo/Redo, Fit/100%/zoom/pan and atomic PNG/JPEG/BMP/PDF Save As. Standard controls retain readable labels and a clear selected tool. The preview has its own identity and does not activate the stable application's settings, updater, startup, tray, file associations or global shortcuts.

Regression checks found and fixed a Fit-state rounding bug. Packaging verification found that the original publish omitted the application XAML resource index; the corrected publish now validates and includes that resource closure. Startup diagnostics are bounded, redacted and confined to startup. Debug symbols remain private.

This is an experimental candidate with native workflow acceptance incomplete. Windows was locked during the native observation. See [verification and limitations](VERIFICATION-WINUI.md); capture, OCR/text editing, recording/audio and other listed migration stages are not available yet.

For a signed portable candidate, extract the entire ZIP into a folder and run `SnipStudio.WinUI3.Preview.exe`, keeping all dependencies, compiled resources and notices together. It is self-contained x64 and does not install over stable Snip Studio. Installation, stable-channel replacement and a public GitHub Release remain separate approval steps.
