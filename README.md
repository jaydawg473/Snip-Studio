<img src="assets/app-256.png" width="72" alt="Snip Studio icon">

# Snip Studio 0.5.5

A Windows screenshot and screen-recording workspace by [Automation Forge](https://automation-forge.com/).

Capture across monitors, annotate images, edit recognized text, and export the result. The editor starts compact and can resize around a snip.

## Capture and edit

- Rectangle, freeform, window, and scrolling capture. **Full screen** lets you choose one monitor; **All monitors** captures the connected desktop.
- Movable pen strokes, highlights, arrows, boxes, and rich text notes, with Undo and Redo. Right-click a mark to select it and switch to Move; use the middle mouse button to pan.
- A pending **Remove area** box also survives switching to **Move**. Drag it within the image or nudge with arrow keys (Shift moves ten pixels), then press **Delete** to apply the removal at its new position. **Escape** cancels. Moving this review box changes no captured pixels, annotations or Undo history; Undo restores pixels after deletion.
- Drag an arrow's tip or tail to rotate or resize it. Drag a text note's side handles to change wrapping width without shrinking its font.
- Double-click a text note, or select it and press F2, to edit font families, point sizes, mixed character styles, ink/highlighting, paragraph alignment, lists, indentation, and spacing.
- Local, on-demand Windows OCR for copying words, snapped highlighting, and line editing. Deleting selected bitmap words masks those words and leaves gaps; it does not reposition the remaining screenshot text.
- Color swatches, recent colors, hex input, and an Exact color honeycomb picker. Tooltips are on by default and can be turned off in Settings.
- PNG, JPEG, BMP, lossless image-backed PDF, clipboard copy, print, external-editor working copies, and Windows Share. Save As is available from the menu and Ctrl+Shift+S.

Source-font matching is an **estimate** from screenshot pixels and installed fonts. Line replacement uses a selectable solid fill; complex backgrounds are not reconstructed. Ambiguous overlapping text is guarded. Exports flatten annotations; notes remain editable in the open editor. PDFs are not searchable text documents or editable forms, and transparency becomes white.

Ctrl+wheel zooms toward the pointer by default; the wheel alone scrolls. Settings can switch to plain-wheel zoom, remember or reset capture delay, and preview screenshot-border color and thickness inline.

The 0.5.5 wheel animation keeps its drawing position separate from native scrollbar updates until the motion finishes or is interrupted. This addresses jitter during rapid wheel input and direction changes while retaining pointer anchoring. Component checks passed; physical wheel feel and painting still need live acceptance.

The More menu anchors to its button and clamps its position to that monitor's working area. Oversized menus use native scrolling. Left/right toolbars contribute their complete measured height to the editor minimum, bounded by the working area; shorter displays keep a scrolling fallback. Physical monitor/DPI placement and pointer dragging still need live acceptance.

## Scrolling capture

Choose **Photo mode → Scrolling**, then select the moving content rather than fixed sidebars or message boxes. Capture advances only while a usable overlap can be verified. Localized animation can be tolerated; broad animation, repeated content, or reflow can stop the capture.

**Finish** opens a preview; **Cancel** discards the capture. Review the joins and bottom before choosing **Use capture**. An early stop or repeated lack of movement produces a partial, **bottom-unconfirmed** result, not proof that the entire page was captured. The previous snip stays intact until the preview is accepted.

Limits are three minutes, 300 scroll requests, 100 accepted frames, 30,000 pixels high, and 64 million pixels. Real applications and physical monitor/DPI behavior still need live acceptance.

## Offline translation

The complete signed release supplies the Bergamot WebAssembly engine, Node runtime, six compact translation models, license notices, and corresponding-source archives. Keep the entire `translation` folder beside `Snip Studio.exe`; extract portable packages completely.

Included languages are **English, German, Spanish, and French**. Models translate English in both directions with each other language; other supported pairs use English as an intermediate step. Choose the source and target explicitly. No Python, Docker, server setup, paid API, subscription, API key, or translation-time model download is needed for the included package.

In Text actions, select words or use all recognized text, then choose **Translate**. Copy the result or use **Replace text in snip** to place replacements in the recognized line bounds. Review wording, font estimates, and solid background fills; one Undo restores the original replacement operation. Compact models and intermediate translation can reduce accuracy. The included workflow does not upload text or images to a translation service.

The public checkout includes the application bridge, patched licensed JavaScript, license text, manifests, and preparation scripts. **It is not a complete, automatically reproducible translation bundle.** Model weights, runtime binaries, bulk downloads, and corresponding-source archives are supplied in release packages rather than this checkout. Preparation scripts require additional metadata and source/download inputs; copying only these sources does not produce a working offline translator.

Upstream projects: [Bergamot](https://github.com/browsermt/bergamot-translator), [Node.js](https://github.com/nodejs/node). Their component notices and licenses remain separate from the application's licensing status.

## Record and make smaller copies

Choose microphone and system-audio devices separately. Recording currently produces H.264/AAC MP4 up to **1920 × 1080 at 15 fps**, with larger areas scaled proportionally.

Recording review offers play, copy, save, trim, and **Make smaller**. Choose 720p, 480p, or a target file size to save a separate copy while preserving the original. Targets are estimates; actual output size is reported, and email/chat attachment limits vary. Smaller exports trade image detail for file size.

Taskbar hiding applies to screenshots and video. Hide taskbars and Keep main monitor taskbar visible default On for new or previously unconfigured profiles; Settings can change either choice. Hidden taskbars are restored after the snapshot or recording ends. Live multi-monitor timing is not established by source or component checks.

## Install, close, and build

Requires Windows 10 or 11 and .NET Framework 4.8. Use a complete signed per-user release installer or extract a portable ZIP. Setup does not launch the app. Its optional sign-in component is unchecked by default. Save work and choose **Exit Snip Studio** before an upgrade; the installer does not force-close running copies.

The 0.5.5 installer asks permission before requesting that an in-use copy close. **No** is the default. Only **Yes** allows a normal close request; unsaved-work, recording, or text-draft prompts may still need attention, and canceling closure blocks the update. Silent installs and diagnostic checks never request closure. Older copies may require a manual **Exit Snip Studio**. The installer stays blocked while files remain in use and never force-closes an application.

Closing an editor closes that snip. **Keep capture shortcuts after closing** is on by default, leaving a small notification-area listener for Ctrl+Alt+S and Print Screen. Choose **Exit** from its menu to stop the listener, or disable that preference to exit with the last editor. Keeping the full editor hidden in the notification area is a separate opt-in. Windows can reserve Print Screen; Snip Studio does not silently change Windows' routing.

Build the application in PowerShell on Windows:

```powershell
.\build.ps1 -UseExistingIcons
```

The C# 5 build uses the installed Windows Framework compiler and WinRT metadata, without NuGet packages. Output defaults to `dist`; a local build is unsigned. The build compiles the app without launching it or running development checks. Optional development checks require separate invocation. Omitting `-UseExistingIcons` compiles and runs the checked-in icon generator.

Selected `checks/Verify-*.ps1` sources cover menu placement, side-toolbar height, removal-selection movement and scrolling with synthetic inputs. Run them separately in Windows PowerShell 5 against a matching, verified signed release, for example:

```powershell
powershell.exe -STA -NoProfile -File .\checks\Verify-MenuPlacement.ps1 -ReleaseFolder release-0.5.5-signed -ExpectedVersion 0.5.5.0
```

The scripts validate the signed assembly before loading it. Their generated reports and images are excluded from Git; these checks do not replace live UI or device acceptance.

See [installer packaging](installer/README.md) for the signed-release inputs. Private signing credentials and the publisher's signing workflow are excluded.

## Files and verification

Original screenshots can save automatically to the chosen folder; Save keeps annotation edits. Editing, OCR, recording, PDF export, compression, and bundled translation run locally. Sharing, external-editor opening, and website visits require a selected action. Uninstall preserves captures, recordings, preferences, working copies, and unlisted files.

The 0.5.5 application has a valid timestamped Automation Forge LLC signature. Against those exact signed bytes, **590 component checks passed**: 181 wheel-frame/anchor, 34 cooperative-exit protocol/lifetime, 25 view-motion, 10 view-preservation, 207 side-toolbar layout, 45 removal-box movement, and 88 shortcut/lifetime/text-replacement checks. They loaded the application as a library using controlled synthetic inputs, without capturing a desktop, displaying an editor or menu, injecting native input, requesting native shutdown, or changing user preferences or the registry. `VALIDATION.txt` records these results and the separately recorded installer source-policy checks; source checks do not establish interactive installer behavior.

Historically, 658 focused checks passed against signed **0.5.4**; those results and earlier scrolling suites do not verify 0.5.5. Physical wheel smoothness, monitor/DPI placement, pointer dragging, native scrolling, Share, audio/taskbar behavior, approved-close/save-cancel interaction, legacy close behavior, and full installation/upgrade/uninstall still need live acceptance. HDR correction remains experimental.

An independent Automation Forge app; not affiliated with Microsoft. No open-source license has been added to this repository.
