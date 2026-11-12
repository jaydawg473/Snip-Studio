# Installer packaging

`SnipStudio.nsi` packages the official Snip Studio 0.5.9 release for the current Windows account. The installer requires Windows 10/11 and .NET Framework 4.8. It creates Start menu shortcuts and offers optional desktop and Windows sign-in components. The sign-in component is unchecked by default. Installation does not launch the application.

From the repository root, point `Build-Installer.ps1` at an installed NSIS compiler:

```powershell
.\installer\Build-Installer.ps1 -CompilerPath 'C:\Program Files (x86)\NSIS\makensis.exe'
```

The script expects `release-0.5.9-signed/` at the repository root containing:

- `Snip Studio.exe`
- `Snip Studio.exe.config`
- `README.txt`
- `VALIDATION.txt`
- `Signature Verification.json`
- `SHA256SUMS.txt`
- `translation/`, with the complete reviewed offline translation payload: original signed Node runtime, bridge, Bergamot JavaScript and WebAssembly engine, all six direct language models, vocabularies, shortlists, licenses, notices, corresponding source archives, and bundle manifest.

The application must be version `0.5.9.0` with a valid timestamped Automation Forge LLC signature. Node must retain its original valid Node/OpenJS publisher signature. Packaging validates the complete recursive translation file list and hashes against the reviewed bundle, then checks `SHA256SUMS.txt` for exact payload coverage and matching bytes. The hash list covers every payload file except itself. Translation install and uninstall entries are generated from that checked file list.

The public checkout includes the bridge source, patched licensed JavaScript, license text, manifests, and preparation scripts. It omits runtime binaries, WebAssembly, model weights, download caches, and corresponding source archives. Preparation requires additional upstream inputs; these sources alone do not automatically reproduce a complete reviewed bundle or signed release. Private signing scripts and credentials are excluded. Publishing these sources does not change the application's existing license policy or the bundled components' license terms.

The output is `installer/output/SnipStudio-0.5.9-Setup.exe`. This script compiles Setup; it does not sign or run it, or launch Snip Studio. The publisher must sign Setup before distribution. The standard NSIS-generated uninstaller is not independently signed.

Verify the installer's embedded NSIS data checksum before signing and again after signing:

```powershell
python checks/Verify-InstallerIntegrity.py path/to/Setup.exe
```

Replace the final argument with the Setup file being released. The check reads the file without launching it and fails on a checksum mismatch. Both the unsigned build and final signed download must pass this gate. Release verification also checks timestamped Authenticode signatures and exact payload hashes; the installer's own integrity checks remain enabled. Python is only a development/release-check dependency. This source packaging script does not sign Setup or perform a native installation.

Before updating or removing installed files, the installer checks whether this installation's application or translation runtime is in use. Closing an editor may leave the shortcut listener running; **Exit Snip Studio** stops it.

In an interactive install, the in-use prompt defaults to **No**. Only an explicit **Yes** requests normal closure of the installed copy. Unsaved-work, recording, or text-draft prompts can delay or cancel closure; Setup rechecks file use and blocks replacement while files remain in use. **No** or **Cancel** stops the update without changing the installed files. Silent operation and diagnostic readiness checks never request closure. Setup never force-closes the app or other programs.

Older copies use a non-forced compatibility request restricted to the identified process and its creation time. It may not finish closing the application. If files remain busy, complete any prompts, choose **Exit Snip Studio** manually, and retry; Setup stays blocked until its file-use check passes.

Uninstall deletes known shipped files and shortcuts, then removes only empty known directories using nonrecursive `RMDir`. Captures, recordings, preferences, working copies, and unlisted additions are retained. The optional startup entry is removed only when its command points to this installation.

The first application launch registers its taskbar identity and an additional per-user image-opening choice for taskbar Recent items. It does not select Snip Studio as the default image application or force a taskbar pin. Uninstall removes that application's image-opening registration only when its command still points to this installation; another copy's registration is retained. Windows owns recent-item privacy and history.

These are source-level packaging instructions. Application component checks and installer source-policy results are recorded in `VALIDATION.txt`; they do not establish live approval/No/Cancel interaction, save-cancel or recording exit, legacy closure, installation, upgrade, uninstall, sign-in startup, or UI acceptance for this release.
