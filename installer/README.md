# Installer packaging

`SnipStudio.nsi` packages the signed Snip Studio 0.6.24 candidate for the current Windows account. It requires Windows 10/11 and .NET Framework 4.8, creates Start menu shortcuts, and offers optional desktop and Windows sign-in components. Sign-in startup is unchecked by default. Installation does not launch the application.

## Publisher prerequisites

This is a publisher-specific signing pipeline. It requires a reviewed timestamp-signed application, a signed PowerShell host and release scripts, signed Python/signing hosts, the publisher's own Azure Artifact Signing credentials, and the reviewed portable NSIS 3.13 toolchain. Credentials and local signing-tool installations are excluded. `Sign-Uninstaller.ps1` shows the maintainer's expected tool layout; modifying it for another environment requires reviewing and signing the modified script before use. These sources alone do not create a signed release.

The reviewed official `nsis-3.13.zip` SHA256 is `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`. See `tooling-source-manifest.json` and the retained `NSIS-COPYING.txt`. Keep the archive beside its extracted `tools/nsis-3.13/` directory. Every compiler input must match that archive. The original compiler/zlib and the two required Unicode plugins were unsigned; reviewed local copies receive timestamped Automation Forge signatures before use. No original valid vendor signature is replaced. The public repository does not contain these PE files or the archive.

From the repository root, after these prerequisites are satisfied:

```powershell
.\installer\Build-Installer.ps1 -ReleaseVersion 0.6.24 -CompilerRoot '.\tools\nsis-3.13'
```

The script requires `release-0.6.24-signed/` containing the signed application/configuration, `README.txt`, `VALIDATION.txt`, `Signature Verification.json`, `SHA256SUMS.txt`, four combined attribution/license files, and the complete reviewed `translation/` payload. Model weights, runtime binaries, bulk downloads and corresponding-source archives are omitted from this source checkout.

The app must be version `0.6.24.0` with a valid timestamped Automation Forge signature. Node retains its original valid timestamped OpenJS signature. Payload paths, hashes, model directions and required notices are validated. The hash manifest covers every payload file except itself.

## Uninstaller and Setup signing

NSIS 3.13's `!uninstfinalize` invokes the signed `Sign-Uninstaller.ps1` hook. The hook signs the full generated uninstaller before embedding, checks the pre/post-signing NSIS CRC and protected bytes, and retains its complete signed copy and receipt. A signing or integrity failure stops compilation. Neither the uninstaller nor installer is executed by the pipeline.

The resulting `installer/output/SnipStudio-0.6.24-Setup.exe` is an unsigned staging file until separately signed and verified. Do not run or distribute that staging file. Verify its NSIS CRC/protected bytes before signing and after signing; inspect the extracted complete signed uninstaller and plugin bytes as well as the outer Setup signature. `checks/Verify-InstallerIntegrity.py` reads artifacts without launching them. Use its `--baseline` and `--report` options for the paired integrity reports, through a verified timestamp-signed Python host.

Fresh 0.6.24 package verification must reconcile Setup, the embedded full signed uninstaller, the application, Node, plugin/tool signatures, exact payload and the 117-source freeze. Prior 0.6.23 package checks do not establish the current package or installation/upgrade/uninstall acceptance. The current release status remains **INSUFFICIENT EVIDENCE**; see `VALIDATION.txt` for test limits.
Before updating or removing installed files, the installer checks whether this installation's application or translation runtime is in use. Closing an editor may leave the shortcut listener running; **Exit Snip Studio** stops it.

In an interactive install, the in-use prompt defaults to **No**. Only an explicit **Yes** requests normal closure of the installed copy. Unsaved-work, recording, or text-draft prompts can delay or cancel closure; Setup rechecks file use and blocks replacement while files remain in use. **No** or **Cancel** stops the update without changing the installed files. Silent operation and diagnostic readiness checks never request closure. Setup never force-closes the app or other programs.

Older copies use a non-forced compatibility request restricted to the identified process and its creation time. It may not finish closing the application. If files remain busy, complete any prompts, choose **Exit Snip Studio** manually, and retry; Setup stays blocked until its file-use check passes.

Uninstall deletes known shipped files and shortcuts, then removes only empty known directories using nonrecursive `RMDir`. Captures, recordings, preferences, working copies, and unlisted additions are retained. The optional startup entry is removed only when its command points to this installation.

The first application launch registers its taskbar identity and an additional per-user image-opening choice for taskbar Recent items. It does not select Snip Studio as the default image application or force a taskbar pin. Uninstall removes that application's image-opening registration only when its command still points to this installation; another copy's registration is retained. Windows owns recent-item privacy and history.

These are source-level packaging instructions. Application component checks and installer source-policy results are recorded in `VALIDATION.txt`; they do not establish live approval/No/Cancel interaction, save-cancel or recording exit, legacy closure, installation, upgrade, uninstall, sign-in startup, or UI acceptance for this release.
