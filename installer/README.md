# Installer packaging

`SnipStudio.nsi` packages the official Snip Studio 0.5.4 release for the current Windows account. The installer requires Windows 10/11 and .NET Framework 4.8. It creates Start menu shortcuts and offers optional desktop and Windows sign-in components. The sign-in component is unchecked by default. Installation does not launch the application.

From the repository root, point `Build-Installer.ps1` at an installed NSIS compiler:

```powershell
.\installer\Build-Installer.ps1 -CompilerPath 'C:\Program Files (x86)\NSIS\makensis.exe'
```

The script expects `release-0.5.4-signed/` at the repository root containing:

- `Snip Studio.exe`
- `Snip Studio.exe.config`
- `README.txt`
- `VALIDATION.txt`
- `Signature Verification.json`
- `SHA256SUMS.txt`
- `translation/`, with the complete reviewed offline translation payload: original signed Node runtime, bridge, Bergamot JavaScript and WebAssembly engine, all six direct language models, vocabularies, shortlists, licenses, notices, corresponding source archives, and bundle manifest.

The application must be version `0.5.4.0` with a valid timestamped Automation Forge LLC signature. Node must retain its original valid Node/OpenJS publisher signature. Packaging validates the complete recursive translation file list and hashes against the reviewed bundle, then checks `SHA256SUMS.txt` for exact payload coverage and matching bytes. The hash list covers every payload file except itself. Translation install and uninstall entries are generated from that checked file list.

The public checkout includes the bridge source, patched licensed JavaScript, license text, manifests, and preparation scripts. It omits runtime binaries, WebAssembly, model weights, download caches, and corresponding source archives. Preparation requires additional upstream inputs; these sources alone do not automatically reproduce a complete reviewed bundle or signed release. Private signing scripts and credentials are excluded. Publishing these sources does not change the application's existing license policy or the bundled components' license terms.

The output is `installer/output/SnipStudio-0.5.4-Setup.exe`. This script compiles Setup; it does not sign or run it, or launch Snip Studio. The publisher must sign Setup before distribution. The standard NSIS-generated uninstaller is not independently signed.

Before updating or removing installed files, the installer checks whether this installation's application or translation runtime is in use. Save your work, choose **Exit Snip Studio** from its tray menu, then retry. Closing an editor may leave the shortcut listener running. The installer does not force-close the application.

Uninstall deletes known shipped files and shortcuts, then removes only empty known directories using nonrecursive `RMDir`. Captures, recordings, preferences, working copies, and unlisted additions are retained. The optional startup entry is removed only when its command points to this installation.

These are source-level packaging instructions. They do not establish live installation, upgrade, uninstall, sign-in startup, or UI acceptance for this release.
