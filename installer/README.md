# Installer packaging

`SnipStudio.nsi` installs an official release for the current Windows account. It creates a Start menu shortcut, offers an optional desktop shortcut, and provides uninstall support. It does not launch the app or enable Windows startup automatically.

`Build-Installer.ps1` accepts a `-CompilerPath` pointing to an installed NSIS compiler. When omitted, it checks the command path, normal NSIS installation folders, and the current user's existing Electron Builder cache.

The publisher packaging workflow expects `release-0.3.0-signed` beside the source and these six files:

- `Snip Studio.exe`
- `Snip Studio.exe.config`
- `README.txt`
- `VALIDATION.txt`
- `Signature Verification.json`
- `SHA256SUMS.txt`

The application must already have a valid timestamped Automation Forge signature. This script compiles Setup; it does not perform signing, install Setup, or launch Snip Studio. Signing credentials and the private release-signing script are intentionally absent.

The uninstaller removes the installed application files and shortcuts, plus an optional startup entry only when it points to that installation. It uses no recursive directory deletion and preserves user captures, recordings, preferences, and working copies. The standard NSIS-generated uninstaller is not independently signed.
