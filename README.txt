SNIP STUDIO 0.6.0 - AUTOMATION FORGE
https://automation-forge.com/

EDITOR TOOL UPDATE - 2026-11-13
Choose Shapes for Rectangle, Ellipse, Line, Arrow and Emoji. Rectangle and
Ellipse support Fill; all four shapes support Outline. Each color channel has
None and its own opacity. Outline also controls Size. Apply accepts the popup
changes once; Cancel leaves the current appearance unchanged. Use Move to select
an existing shape and change its style. Undo restores its previous appearance.

Choose Emoji, pick a symbol, then click the snip to place it. Emoji stamps are
editable text: use Move to reposition them or Text / Advanced text to edit and
resize them. Their appearance follows the Windows fonts available on the PC.
Escape or choosing another tool cancels placement before a stamp is added.

Choose Text, click the snip and start typing directly on the image. Enter adds
a line; Ctrl+Enter or Done finishes the edit; Escape or Cancel discards the draft.
Click outside the draft to apply it before the next canvas action. Existing notes
can be edited directly too. Advanced text... opens the full formatting editor,
including fonts, alignment and exact text-box size. Its Cancel retains the draft.
Right-click while typing for Cut, Copy, Paste, Select all and Advanced text.

Choosing an annotation tool keeps the ordinary arrow pointer. Actual editing can
still use move, resize and text-selection cursors where they provide useful feedback.
Toolbar placement remains available at Top, Right, Bottom or Left in Settings.
Tool selection preserves the snip's zoom and image anchor.

INSTALL AND SAVED SETTINGS
Use SnipStudio-0.6.0-Setup.exe, or extract the complete signed portable ZIP.
Setup retains the cooperative save/close/cancel update flow. Save your work when
asked; canceling closure cancels replacement. Existing saved settings remain in
the user's app-data folder; the preferences schema is unchanged in this update.
The complete translation folder, models, licenses and sources remain included.

VERIFICATION AND LIMITS
The C#5/.NET Framework 4.8 application built without compiler warnings. Its
Automation Forge signature and timestamp verified Valid. Exact application SHA256:
C540441E0C17FEBC4C820B37513BD5775B9C973FE0A059743F6DAE99EF5201C2
513 signed-library component checks passed, including 121 editor-tool checks,
78 crop, 181 zoom-frame, 45 removal-selection and 88 translation checks.
2,442 unshown layout checks covered eight toolbar/window/scale fixtures and nine
popup fixtures. 73 installer close source-policy checks passed. Layout scaling
fixtures are not physical DPI testing; inline layout uses a presence fixture,
while real unshown RichEdit text/commit/render behavior has separate component checks.
No desktop capture, physical input, production installation, or live popup focus
acceptance is established by those checks. Final installer signature, CRC,
payload reconciliation and read-only readiness evidence are recorded separately
for the exact delivered files. Historical results below apply only to their versions.

HISTORICAL 0.5.9 README FOLLOWS

SNIP STUDIO 0.5.9 — AUTOMATION FORGE
https://automation-forge.com/

Install with "SnipStudio-0.5.9-Setup.exe", then open Snip Studio from the
Start menu. Installation is for your Windows account, with an optional desktop
shortcut. It does not launch the app or enable Windows startup automatically.
The optional "Start Snip Studio at Windows sign-in" installer component is
unchecked by default. It uses the saved background preferences at sign-in;
installation itself does not start the listener or editor.
When installed Snip Studio files are in use, Setup asks Yes/No/Cancel before attempting to close that installed copy. No is the default. Yes requests a normal exit with save/cancel prompts and recording finalization; it never forces termination. No or Cancel stops without replacing files. Setup waits, then checks file locks again. Older versions may not exit their shortcut listener automatically; if files stay in use, choose Exit from the tray and Retry. Silent installation and /CHECKREADY never close applications.
Uninstalling keeps your screenshots, recordings, and preferences.
Updating to 0.5.9 reuses your existing saved app settings; the crop feature does
not reset them or add new preference fields.

For portable use, extract the ZIP and run "Snip Studio.exe". Keep the matching
.exe.config beside it. Requires Windows 10/11 with .NET Framework 4.8.
No account is needed. If an older or different portable copy is still running, startup now explains the conflict instead of silently opening that copy. Save your work, choose Exit Snip Studio from its tray menu, then reopen the installed app.

TASKBAR RIGHT-CLICK MENU
Open Snip Studio once, then right-click its Windows taskbar icon. The Tasks
section offers Take a new screenshot and new screenshots in 3, 5, or 10 seconds.
These actions can start the app when it is closed or use its existing listener.
Take a new screenshot has no delay. Each timed action applies to that one snip;
it does not change the clock's saved delay or Remember capture delay setting.
Taskbar screenshot actions use Photo mode. A previous Scrolling choice returns
to Rectangle so it cannot bypass the requested countdown.

If the taskbar menu is missing or stale, choose More > Refresh taskbar menu.
The same action is available from the notification-area menu, including when
only the capture-shortcut listener is open. Refresh runs in the background and
reports the completed registration attempt and any warnings; repeated requests
are combined rather than starting unlimited work.
Startup also refreshes the menu and updates the taskbar identity on matching
Snip Studio Start menu, desktop and pinned shortcuts. It changes shortcut
metadata only when the target is this exact application copy; it does not force
a pin/unpin or change the default image application. Windows controls its
taskbar cache, so a completed refresh is not proof of immediate visual change.

Recent lists successfully opened or saved PNG, JPG, JPEG, BMP, GIF, TIF, and
TIFF images, including originals saved automatically after capture. Select a
recent image to reopen it in Snip Studio. Unsaved clipboard images, canceled or
failed saves, PDF exports, and recordings are not added to this image list.
Windows manages the list and its privacy controls. Recent items may be hidden
when Windows' recent-item setting is off. Windows controls which items appear;
Snip Studio does not rebuild a separate list. Image defaults are not changed.
Pin to taskbar, Unpin, and
Close window are Windows commands and depend on the icon's current state.

UPDATE NOTIFICATIONS
Check for updates automatically is On by default. When Snip Studio opens, it
checks the official GitHub releases in the background at most once a day.
Turn this off under Settings > Updates > Check for updates automatically.
More > Check for updates and the notification-area menu offer a manual check.
Manual requests still respect short cooldowns and GitHub rate limits.
After a transient connection failure, a manual check can retry after ten
seconds. Connection and cancellation outcomes are reported separately;
unavailability is not presented as an up-to-date result.
When a newer release is found, choose Download or Download update to open its
official installer link in your browser. Snip Studio does not silently download,
install, or run an update. You choose whether to download and run Setup.
Update requests include the app version, not screenshots, recognized text,
preferences, or machine/user identity. A failed check leaves current work intact
and reports that the service is unavailable rather than claiming up to date.

CAPTURE
Choose Photo mode, select a capture mode, then New. Full screen lets you click one monitor to capture; All monitors captures the complete connected desktop. Rectangle, Freeform, Window, and Scrolling remain available.
Selections span the entire virtual desktop, including across monitors.
Escape/right-click cancels selection. Delayed capture has a Cancel button.
The app starts compact and can resize to fit a new snip. The clock shows the
selected delay (Off, 3s, 5s, or 10s).
Normal capture closes app menus and hides the initiating editor and Settings
windows for the snapshot. Other open snips stay visible so you can capture them.
Use More > Capture open snips to include the initiating editor's snip as well;
that one capture keeps the editors visible while hiding Settings. A previous
Scrolling choice uses Rectangle for this shot, then keeps your chosen mode and
timer. This is a one-shot action rather than a saved visibility preference.
Hidden windows' fade transitions are suppressed before restoration. Starting a
hidden-window hotkey capture does not first show and hide the editor.
Remember capture delay is on by default. Turn it off in Settings to start
each new app session with no delay; you can still choose a delay while open.
Window capture copies the visible desktop area, including overlapping content.
Hide taskbars during capture and Keep main monitor taskbar visible are both
on by default. Previously unconfigured profiles adopt taskbar hiding once;
the saved main-taskbar choice is retained. Later explicit choices are remembered.
Change either option in Settings.
Hidden taskbars return after the screenshot snapshot or when recording ends.

EDIT
Move selects and drags annotations and removal selection boxes. To adjust a removal area before deleting, switch between Remove area and Move; drag the box or use arrow keys (Shift moves ten pixels). Delete applies at the new position; Escape cancels. Moving the box changes no pixels or Undo history. Drag blank space with the middle mouse
button to pan the image. Arrow keys nudge; Shift+arrow moves ten
image pixels. Escape cancels a move. Delete removes the selected annotation.
The pointer shows the active drawing tool's icon. Right-click an annotation
to select it and switch to Move, then drag it or use its available handles.
Pen, Highlight, Arrow, Box, Text, Eraser, Crop, Undo, and Redo are available.
Choose Crop to open a frame around the full image. Drag any of its eight edge
or corner handles to resize the frame, or drag inside it to move the frame.
The frame previews the crop. Choose Apply crop or press Enter to apply it;
choose Cancel or press Escape to keep the image unchanged. An applied crop
creates one Undo step and preserves editable annotations in the retained image.
Applying the unchanged full-image frame leaves the image and Undo history
unchanged. Entering Crop keeps the current zoom and image center.
Eraser removes marks rather than pixels.
Highlight snaps to recognized words/lines when enabled. Shift-drag forces
freehand highlighting. Recognition progress and unavailable states are shown.
Text notes support rich text. Double-click a note in Move/Text mode, or select
it and press F2, to edit its words and formatting. Select part of the draft to
change only those words, or Select all to format the whole note. Controls
include font family, point size, bold, italic, underline, strikethrough,
superscript, subscript, ink color, highlighting, and clear formatting.
Paragraph controls provide left/center/right/justified alignment, bullets,
numbering, indentation, line spacing, and space before/after paragraphs.
Cut, Copy, Paste, Select all, and draft Undo/Redo are available. Supported text
formatting is kept; embedded objects are not pasted into the text draft.
Drag the side handles to make a note narrower; it wraps and grows in height.
Box width changes wrapping without changing the chosen font size. The editor
shows its preview scale; keep the note inside the screenshot edges.
In Move, select an arrow and drag its tip or tail handle to rotate or resize it.
The color palette offers swatches, recent ink colors, and exact hex values.
Click the Exact color square to open the honeycomb picker with RGB, hex,
brightness, and current/new previews. Cancel leaves the previous color intact.
Wheel zoom and scroll use short animated transitions. Zoom frames keep their floating image origin separate from native scrollbar state, reconciling the scrollbar range only when the burst ends or is interrupted. Ctrl + mouse wheel
zooms toward the pointer by default; the wheel alone
scrolls. Turn off Hold Ctrl to zoom with the mouse wheel in Settings if you
prefer plain-wheel zoom. Actual size (100%) is available in More > Zoom.

COPY, TEXT, PRINT, SHARE, AND EDITORS
Copy image / Ctrl+C copies the rendered snip. Paste / Ctrl+V opens an image.
Copy text / Ctrl+Shift+C adds a selectable text overlay directly on the image.
Recognition starts when Text actions or text-snapping Highlight needs it;
ordinary capture/editing does not continuously run OCR. Wait for the readiness
message before selecting recognized words.
Drag over words to select them, then copy the selection or all recognized text.
Right-click selected words for Copy selection without changing the selection.
Text actions show a bright preview outline for at least one second while the
commands become ready. OCR can take longer. The screenshot stays at full
contrast; selected words receive a blue band. These preview marks are not saved.
Review recognized text for possible misreads.
Select words within one recognized line, then Edit line (or F2) to replace
words in the screenshot. The selected words start highlighted in the editor.
Delete selection masks only the selected bitmap words with a solid fill.
The remaining screenshot words keep their positions, so gaps can remain.
These edits support Undo. Edit line estimates the source font from screenshot
pixels and installed fonts. It is an ESTIMATE, not recovered font metadata;
review the family, size, style, and spacing and adjust them when needed.
Line replacement covers the old line with a solid fill, whose color can be
changed in the editor. Complex image backgrounds are not reconstructed.
For text you already added, edit the original text note. Ambiguous overlapping
bitmap/vector text is left unchanged rather than guessing what to remove.
Save always opens Save As to choose a filename, location, and PNG, JPEG, BMP,
or PDF format. More > Save as PDF is also available.
Save As is always available from the menu and Ctrl+Shift+S, even when the
current snip already has a filename. It asks for a filename and location.
PDF keeps a lossless image of the completed snip; it is not a searchable or
editable text document. PNG preserves transparency. Image exports are
flattened; editable annotations remain in the open Snip Studio window.
Print / Ctrl+P provides preview, printer selection, and page setup. A Windows
Print dialog confirms the job before submission.
Share on the main toolbar or More > Share opens Windows' share panel. You
choose the destination; no recipient is selected automatically.
Settings lists detected editors, including Photoshop when installed. Refresh
the list or browse manually if needed. Choose an editor, then Edit in ...
opens a separate PNG working copy. Save it there, then use More > Reload
external edit to load the changes into Snip Studio.

TRANSLATE RECOGNIZED TEXT
The complete 0.4.2 package includes a free, open-source Bergamot translation
engine running locally through WebAssembly and a bundled Node runtime.
No Python, Docker, localhost server setup, Azure, paid API, subscription,
or API key is required for the included offline translation workflow.

Included languages: English, German, Spanish, and French. Six small models
cover English in both directions with each of the other three languages.
Other supported pairs use English as an offline intermediate language.
Choose the source language explicitly, choose the target, then Translate.
Automatic language detection and other languages are not promised by this
included model set. No model download is performed while translating.

In Text actions, select recognized words or leave the selection empty to use
all recognized text, then choose Translate. The dialog keeps the original
beside the result, supports Cancel, and offers Copy translation.
Replace text in snip translates each recognized line and places its replacement
in the corresponding source bounds. It estimates font and ink color, covers the
old bitmap words with a sampled solid background, and fits/wraps the result.
This is not perfect background reconstruction: review the result, especially
on textured or complex backgrounds. One Undo restores the original bitmap and
removes the replacement annotations together.
Offline translation does not upload the image or text to a translation service.

Keep the entire translation subfolder beside Snip Studio.exe, including its
runtime, worker, models, bundle manifest, and license/attribution files.
The portable ZIP must be extracted completely; copying only the application
EXE omits the included translator. If files are missing, repair or reinstall
the complete package. The bundled Node executable keeps its original valid
publisher signature; Automation Forge signs the application and Setup.

Translation output can be inaccurate, especially with compact models or an
English intermediate step. Review important values and wording. All six direct
model directions and an English-pivot route passed synthetic phrase checks;
broad language quality and live dialog operation remain unverified. Components
are packaged only after their license review is complete.

Bergamot source:
https://github.com/browsermt/bergamot-translator
Node source:
https://github.com/nodejs/node

VIDEO
Choose Video mode, then Record to select a region across monitors and start
after a countdown. Use the audio button to choose microphone and system-audio
devices separately, or Windows defaults.
Stop finalizes local H.264/AAC MP4, up to 1920x1080 at 15 frames/second.
Larger regions are scaled proportionally. Unavailable devices report an error.
Windows capture exclusion is requested for the controller when supported;
otherwise place it outside the selected region.
Review a recording to play, copy, save, or trim a new copy. Trimming re-encodes
H.264/AAC and preserves the original. Copied videos are file clipboard items;
the receiving app must accept video files.
Make smaller in recording review saves an email-sized copy using Balanced
720p, Small 480p, or a target size (10/20/25 MB or custom). Progress and Cancel
are available. The original stays intact. Estimates are not hard size limits:
the result reports its actual size and whether the chosen target was met.
Very compact or long videos may need trimming to meet a smaller target.

SETTINGS
More > Settings includes automatic copy/save, folders, edited-file prompts,
multiple windows, border color/width, HDR correction, audio defaults/devices,
Dark/Light/System theme, compact startup, automatic resizing, preferred editor,
Print Screen, notification-area operation, and optional background startup.
Settings defers installed-editor/audio discovery and shell-icon work until
after the form appears. Loading and failure states preserve saved choices.
The screenshot-border section expands inline and provides a live sample with
color and thickness controls. Preview changes before saving Settings; Cancel
discards the settings draft. The preview is available even while borders are off.
Show tooltips is on by default and can be turned off in Settings.
Automatic image copies replace the clipboard when enabled.
Original-capture saving and preparation of image clipboard copies run in the
background. Status messages report progress or failure. This implementation
change has not yet established live smoothness on a user's computer.
Original captures can save automatically; edits save when you select Save.
Unstored captures still prompt if an automatic save could not complete.
HDR correction uses float-format Windows Graphics Capture and tone mapping;
unsupported paths report errors rather than silently applying a color filter.
HDR correction is experimental in this build: live HDR fidelity is unverified,
and the conversion currently assumes an 80-nit SDR white level.

SHORTCUTS
Ctrl+N New; Ctrl+O Open; Ctrl+S Save; Ctrl+Shift+S Save as;
Ctrl+C Copy image; Ctrl+Shift+C Copy text; Ctrl+V Paste; Ctrl+P Print;
Ctrl+Z Undo; Ctrl+Y Redo; F1 Help.
Ctrl+Alt+S captures from another app while Snip Studio is running.
Print Screen capture is enabled by default in Snip Studio. Windows may reserve
the key or route it to Snipping Tool; use the
provided Windows keyboard settings link to review that choice.
Snip Studio does not silently change Windows' Print Screen routing.
Close closes the current editor. Keep capture shortcuts after closing is on
by default: closing the last editor leaves a small notification-area listener
so Ctrl+Alt+S and Print Screen can still start a capture. Choose Exit from its
notification-area menu to stop the listener and shortcuts. Turn this setting
off to exit when the last editor closes.
Keep running in the notification area is a separate opt-in setting. When on,
Close hides the full editor and preserves its current snip for reopening.
Start with Windows is also optional and off by default. Enable it in Settings
or select the optional installer component. With the default capture-shortcut
preference, it starts only the listener. With notification-area editor mode on,
it starts with the editor hidden; if both background preferences are off, it
opens the main window. Existing saved background preferences are respected.

YOUR DATA
Editing, recognition, recording, and bundled translation run locally. The
included translator does not send the text or image to a translation server.
Sharing, opening an external editor, and browser visits happen when selected.
Automatic update checks contact the official GitHub release endpoint by default;
turn them off in Settings > Updates if preferred. Requests include the app
version, not capture content, recognized text, preferences or user identity.
Windows keeps recent-image paths locally for the taskbar menu, subject to its
recent-item settings. Snip Studio does not upload or keep its own recent list.
Preferences: %LOCALAPPDATA%\SnipStudio\preferences.xml.
Share/editor/video working copies stay in that local application folder so
other apps can finish reading or editing them after a dialog closes.
Protected content may appear black. Scrolling capture is available in Photo mode > Scrolling. Select only the moving content, leaving out sidebars and fixed message boxes. Small animations are tolerated only when a unique overlap remains verifiable. Broad animation or streaming/reflowing content can still stop capture. Smaller-step retries briefly reverse only the pinned scrolling target and continue only after the previous view is verified. Finish opens a preview; Cancel discards. Review the joins and bottom before Use capture. Partial results do not confirm that the bottom was reached. Limits are three minutes, 300 scroll requests, 100 accepted frames, 30,000 pixels high and 64 million pixels.

DEVELOPMENT
Source is beside build.ps1. Build without running helpers or checks:
.\build.ps1 -UseExistingIcons
The optional -Check switch runs development check executables.
The build uses the Windows Framework C# compiler and system WinRT metadata.
Native video checks: .\checks\video\run-checks.ps1
-LiveAudio deliberately checks actual audio devices.
Icon sources and regeneration tool are in assets\ and tools\.

An independent Automation Forge app; not affiliated with Microsoft.

0.5.9 VERIFICATION STATUS
The application builds without compiler warnings and has a Valid timestamped
Automation Forge signature. 593 signed-library checks passed: 392 core checks
(78 crop-frame, 88 translation, 181 zoom-frame and 45 removal-movement) plus
201 offscreen crop-layout checks across eight toolbar/dimension/scale cases.
73 installer source-policy checks passed separately. Two synthetic offscreen
renders were visually inspected for full and adjusted crop frames and handles.
The preferences source is identical to 0.5.8; existing saved settings are reused.
See VALIDATION.txt for exact application bytes, coverage and limits.
Actual mouse dragging and physical DPI/keyboard behavior, native capture and
taskbar behavior, installed-upgrade behavior and endpoint-security acceptance
still require live checks. Earlier release counts do not add to these totals.

HISTORICAL 0.5.8 VERIFICATION STATUS
The final application has a Valid timestamped Automation Forge signature.
689 signed-library checks passed: 501 core component checks, 73 synthetic
menu-placement checks and 115 offscreen update-UI/preferences fixture checks.
The core set includes 122 Jump List, 30 taskbar refresh, 174 update checker,
25 capture visibility, 88 translation/request, 34 installer-exit and 28 startup
checks. 73 installer source-policy checks passed separately. Offscreen and
synthetic checks do not prove actual monitor/taskbar capture behavior.
The exact signed product also completed a live update check from its own
.NET Framework 4.8 host, using Windows/SystemDefault TLS with strong crypto
enabled. It exited successfully with an up-to-date result and left preferences
unchanged. This was not a PowerShell-hosted network check and did not initialize
the editor, capture, shortcuts, or Shell integration. The earlier 0.5.7 connection
failure's exact cause remains unconfirmed; other network setups still need
acceptance. No certificate-validation bypass, silent download or automatic
installation is introduced. See VALIDATION.txt for exact bytes and scope.
Actual capture/restoration across open snips and monitors, all cancellation
paths, native taskbar/cache behavior and full installation/upgrade/uninstall
still need live acceptance. Earlier release counts do not add to these totals.

HISTORICAL 0.5.7 VERIFICATION STATUS
The final application has a Valid timestamped Automation Forge signature.
418 signed-library checks passed: 122 Jump List, 30 taskbar refresh, 116 update
checker, 88 translation/request, 34 installer-exit and 28 startup checks.
73 installer source-policy checks passed separately. See VALIDATION.txt for
the exact signed application hash and coverage. Package/installer evidence is
recorded separately; these checks do not establish native taskbar appearance.
A user-approved native close diagnostic on a signed installer candidate closed
the installed 0.5.6 listener without changing its executable or installing files.
That candidate contained an older payload. This does not identify the cause of
the earlier reported 0.5.5 tray-close failure or prove the final full upgrade.
Taskbar cache/pin behavior, save-cancel/recording shutdown, full installation and
device/UI acceptance remain unverified. If files stay busy, save work, choose
Exit from the notification-area menu and Retry, or cancel Setup.

HISTORICAL 0.5.6 VERIFICATION STATUS
The application builds and has a Valid timestamped Automation Forge signature.
272 signed-library checks passed, including 122 taskbar command, dispatch and
registration-wiring checks. A further 35 in-memory native Windows Shell-link
round trips and 39 installer source-policy checks passed. See VALIDATION.txt
for the exact application hash and coverage. Native taskbar appearance, existing-pin
refresh, Windows recent-item privacy behavior, real capture/countdown and
installation/upgrade/uninstall require live acceptance; parser or library
checks alone do not establish those outcomes.

HISTORICAL 0.5.5 VERIFICATION STATUS
590 signed-library app checks passed, including 181 deterministic wheel-frame checks and 34 cooperative-updater-exit protocol/lifecycle checks. 38 installer source-policy checks passed. The previous signed0.5.4 reproduced per-frame native scrollbar churn. These checks do not establish physical wheel feel, native close prompts, recording shutdown or full installation/upgrade/uninstall; those still need acceptance. See VALIDATION.txt for exact coverage and limits.
