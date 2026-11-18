SNIP STUDIO 0.6.21 - AUTOMATION FORGE, 2026
https://automation-forge.com/

TEST BUILD - TYPOGRAPHY REMAINS APPROXIMATE
This version refines recognized-text regions before inline editing. Strong
graphic prefixes are separated from text before estimating its font, color
and background-erasure area. Recognized columns keep separate hit-test regions,
including when returning to an already edited line. Ambiguous graphic/text
boundaries use conservative checks rather than treating every symbol as an icon.

The selected text color now applies from the first typed character in new text
boxes. Text color controls follow the active text selection or caret and describe
mixed colors; explicit color choices apply to that text. Returning to drawing
retains the chosen drawing ink. Source colors remain estimated from pixels.

Palette opening and closing keep the current window bounds. Fit recalculates
for the available image area and clears stale scroll extents, so the whole snip
stays visible. Manual zoom/pan retains its view; closing restores the original
view rather than leaving extra horizontal canvas space.

The exact signed 0.6.21 application has 7,132 passing assertion executions across
11 accepted current jobs. Four synthetic/actual-control images were reviewed.
OCR spelling and raster typography remain approximate; the global font matcher
is unchanged. No exact source-font recovery or pixel-perfect editing is claimed.
See VALIDATION.txt for the exact application identity and evidence limits.

RECORDING AREA AND DESKTOP OUTLINE
Choose Draw area to drag a custom rectangle, or Window to click an application
window. Window selection records a fixed screen rectangle, including any other
window covering it. It does not follow a moving window or recover hidden target
content.
The existing microphone and system-audio choices remain available.

The recording outline shows the selected area on the desktop. Its segments
stay outside the captured pixels wherever possible. At screen edges, an inset
segment is shown only when supported Windows capture exclusion is confirmed.
If Windows cannot exclude a required inset segment, that segment stays hidden
and the recording panel reports the incomplete outline. The outline is removed
when recording ends, fails or is canceled. See VALIDATION.txt for the exact
verified scope; live desktop and saved-video behavior need acceptance testing.

The MP4 filmstrip, compact tool options and fixed editor shell remain available.
The Windows OCR engine and font matcher are unchanged; editable-region processing is updated. Source-font identity, size, weight, color
and layout remain estimates; pixel-perfect typography is not established.
Prior 0.6.20 recording, native-media and layout checks are historical evidence;
their 998 assertion executions are not current 0.6.21 test results. Live desktop
recording exclusion, physical monitor behavior and installed upgrades remain
outside unshown-control and geometry checks.

VIDEO REVIEW AND CLIPCHAMP
Video review gives its preview the available width and height, with compact
metadata below and scrolling when the controls cannot all fit. Edit in Clipchamp
requests the installed app's registered file action when available. Otherwise,
it opens Clipchamp and shows instructions to import the current MP4. A successful
launch request does not prove that Clipchamp finished importing the file.
Missing registration or a launch failure is reported in the review window.
This action does not change the default MP4 application or install Clipchamp.
Clipchamp remains a separate Microsoft application with its own account and
service behavior. Snip Studio does not launch it until the button is selected.

MATCHING TOOL OPTIONS
Selecting a tool with options opens its matching compact submenu. Clicking the
main tool button again keeps those controls open; its arrow or the submenu's
close button can close them. Clicking the main button with its submenu closed
opens it again. A new image can automatically open options for the active
default Pen. A submenu the user deliberately closed is respected.

Automatic options opening for a new image participates in that image's initial
fit. Deliberate tool actions, zoom, pan and window moves/resizes retain their
existing safeguards against a late automatic fit.

BACKGROUND COPYING AND ACTIVE TEXT
Automatic image copying prepares a snapshot of committed snip content without
committing the text currently being typed. A late background copy leaves the
inline caret and unapplied draft active. Clipboard retry and stale-request
guards remain. Finish a draft to include its text in committed snip content.

MP4 FILMSTRIP AND TRIM
Open a recording for review to see still-frame thumbnails along its timeline.
Click or scrub for a decoded preview frame, drag the blue start/end handles,
or enter start/end seconds to three decimal places. Set start and Set end use
the current preview position. Apply trim exports a separate MP4 and preserves
the source file. Save as, smaller-video options and file copying remain.

The filmstrip previews still frames; Play video opens the default external
video application for playback with audio. The nearest decoded preview frame
is not a frame-exact editing guarantee. This is a single trim range rather
than a multi-track editor, clip reordering or split-cut timeline.

The filmstrip UI and the extension of the existing Windows Media Foundation
backend are original Snip Studio implementation. Microsoft's VideoThumbnail
sample was a technical reference. The pinned reference and complete MIT
license are included in About > Open-source licenses and translation credits.
The feature uses Windows media APIs offline without an added video engine
binary, paid media service or runtime download.

FIXED LAYOUT AROUND THE IMAGE
The editor measures room for its widest text commands and largest tool-options
row when an image opens. That top space stays reserved while tools and their
normal submenus change, keeping the window, viewport and image position steady.
The image keeps a consistent surrounding margin. Footer help uses one line;
hover its text for the full message. Narrow command rows scroll horizontally.

Initial fitting for a new image and a deliberate window resize can change the
view. Merely selecting a tool is not a new image fit. The compact empty
launcher is unchanged. See VALIDATION.txt for the signed-build cycle checks
and physical-interaction limitations.

TOP-ROW TEXT
The supplied header's sender, recipient and first body line were already
recognized in the earlier build. Prior fixed-layout checks targeted all three
lines through tool cycles without measured point drift using managed handlers.
Those historical counts are not current 0.6.21 evidence. The OCR engine is
unchanged.

RETURNING TO AN EDITED NOTE
In Text mode, click one edited note, another note, then the first again to
place the caret directly. Existing notes are checked before resize handles
or a recognition refresh. Drag and double-click word selection remain usable.
Invalid or unapplied drafts keep their existing safeguards.

TEXT NAVIGATION AND NONBLOCKING FEEDBACK
Single-clicking recognized text places the caret without selecting a whole
word. Drag to select a range; double-click still selects a word. Switching
between recognized lines and their existing editable notes keeps the command
row and image view stable. Cached recognition is reused only while its image
and document revision remain current; stale work is rejected.

Find text and inline text commands use the top space reserved when the image
opens. Entering or leaving them keeps that image viewport in place. Monitor
limits still apply to initial sizing, and manual window resizing remains usable.

Translation opens in an owned modeless window. Other snips remain available.
Replacement warnings stay in the translation window; inline-edit warnings use
the editor's status and keep the draft available. Advanced text remains an
explicit formatting dialog. A changed snip cannot receive a stale translation.

CLIPBOARD RECOVERY AND STEADIER SCROLLING
Image copying retries a busy clipboard asynchronously, with six attempts over
up to 800 ms of waiting. A newer copy or changed snip cancels the stale request.
If copying remains unavailable, the footer offers Copy image to retry without
blocking other snip windows with a warning dialog. Original saving is separate.

Scrolling and resting use the same image filter and pixel-aligned position.
Drawing, text hit testing and the caret share that position. Active zoom keeps
continuous positioning. Clicking the canvas cancels delayed initial fitting.

NATIVE-SIZE OPENING AND REUSABLE TEXT
New snips grow their window first when the current monitor working area has
room for the image at 100% plus its tools and footer. The settled initial fit
rechecks same-size replacements and late footer/DPI layout. Images scale down
when the working area cannot fit them. The resize-to-snip preference, maximized
and full-screen views remain respected. Interactive window move/resize cancels
pending automatic growth, as do deliberate zoom/pan and edits.

After recognition, switching tools keeps a valid text session available.
Return to Text and click a recognized line to edit it; blank areas still allow
new text boxes. Drawing/Move tools keep their ordinary gestures. If the image
or document revision invalidates the result, Text refreshes it automatically.
A click during that refresh may open the current line after recognition; newer
clicks, tool changes, cancel, image changes and failed requests reject stale work.

Font matching can use up to three original text regions to estimate a common
family, then fits each line's size, bold and italic appearance separately.
Strong evidence for a different family can override that shared hint. The hint
is cached across the app's own line edits, so the first edit can take longer.
Candidate selection is an estimated starting point that may need adjustment.
Color sampling accounts for colored ClearType fringes while
requiring support from original
pixels. Font family, weight, size and color remain estimates from screenshot
pixels; OCR spelling and source-layout reconstruction are separate limitations.

TRANSLATION APPEARANCE AND IDENTIFIERS
Translation validates the original line with the same renderer as direct text
editing before placing its replacement. Recognizable part/model identifiers,
common file extensions, URLs and email addresses are kept verbatim while prose
around them is translated. OCR errors already present in those identifiers are
not corrected by this protection. Ordinary prose retains full-line context when
no protected spans are present; surrounding prose is translated in separate
pieces when a line contains protected identifiers.
Lines whose translated text is unchanged keep their original pixels, including
their exact font and color. An entirely unchanged result adds no history entry.

Translation first tries the estimated source font size in the original line.
If more width is needed, it checks bounded blank space to the right before
shrinking the text. Nearby recognized words, lines and annotations limit that
space; background pixels must match exactly. The original text-erasure area
does not expand. Longer translations may still need smaller text. All lines
are checked before any replacement is applied, and Undo restores the original
image text. Exact original font recovery and arbitrary mixed formatting within
one recognized line are not guaranteed.

STEADIER TEXT EDITING AND COMPACT FIT
Switching recognized text lines keeps the edit command row in place while the
next font is matched. The invisible keyboard editor is isolated from the image
scroll extent, so its large text layout does not add image scrollbars.
New snips receive a settled-layout fit; deliberate zoom/pan or editing cancels
late fitting. Small snips retain native scale when space permits. Gray padding
is reduced, and auto-sized windows use measured content height rather than a
fixed 380-pixel floor. Toolbar/screen limits still apply.

ZOOM PERCENTAGE SELECTION
Idle zoom percentages display as plain text. Programmatic view updates, preset
commits and leaving the field clear selection without moving keyboard focus.
Deliberate text selection, Ctrl+A and pending typed percentages remain usable.

CAPTURE RECOVERY
If Windows is slow or refuses to hide a taskbar, a still snip now continues
with a visible status warning that a taskbar may be included. A bounded retry
helps Explorer settle. Keep main monitor taskbar visible is still honored.
Recording keeps its existing strict taskbar checks.

Capture errors restore the desktop/application state and release capture
ownership before showing a modeless notice. The notice does not require an OK
click before other snip windows can continue. Starting New closes the previous
notice. Genuine taskbar restoration failures are still reported.
If an old-version modal capture warning is still open, dismiss it once before
running the update so the old application can finish its capture cleanup.

TEXT DELETION SIZE
The first edit of recognized text now chooses a nearby font size by actual
source glyph dimensions before comparing normalized letter shape. This fixes
cases where deleting a letter revealed an oversized initial estimate.
Font size and mixed formatting remain unchanged during typing/deleting.
Font matching remains an estimate; Advanced text is available for adjustment.

FONT APPEARANCE AND CARET
Edited text now preserves antialiased stroke coverage instead of dropping
ClearType edges. Matching also considers available Segoe UI Variable variants.
When editing a recognized line, a bounded background comparison at actual
pixel sizes prepares candidate appearances. The final text renderer compares
nearby sizes with independent slant and stroke evidence. Short-label matching
can compare separated glyph coverage; its character-count check is heuristic
and abstains when source glyphs are ambiguous. This work runs when an edit
starts, with cancellation; it is not repeated for each typed letter.
Fonts remain estimates from screenshot pixels; the measured output is not an
exact reconstruction of the original font or layout.

Recognized-text carets use cached source-scale layout instead of remeasuring
a zoomed hidden editor. This keeps click positions aligned with the displayed
text at fractional zoom. Untouched source words retain their OCR positions.
Wrapping, paragraph ends, trailing spaces and common grapheme clusters use
logical insertion boundaries; the native editor continues to handle typing.

TEXT ENTRY
Draw a text box, release, and type directly inside its dotted boundary. The
frame keeps the size you draw; overflowing text scrolls inside it while editing.
Enter adds a line. Ctrl+Enter or Done applies; Escape or Cancel discards the draft.
Advanced text remains available through the text options or editor right-click.

RECOGNIZED TEXT
Choose Text actions, then click a recognized word to edit its line in place.
No visible field or dotted box covers recognized text. Untouched source pixels
stay exact until an edit is made. Click another recognized line to continue
editing without reopening Text actions. Changed lines become editable notes;
old source words are not resurrected after deletion. Pure tool changes retain
valid recognized text for returning to Text. Image changes, Undo or a crop can
invalidate recognition; Text refreshes stale geometry when needed.

The app compares common available font families and regular/bold/italic styles
against the source letter shapes when a line is edited. Source position, size
and color guide the replacement. Screenshots do not contain font metadata;
matching is an estimate. Advanced text remains available to adjust formatting.
Cancel or an unchanged edit keeps original pixels; Undo restores applied edits.
Photos and gradients may show a sampled flat background patch after replacement.

TRANSLATION COLORS
Replace text in snip samples each source line's original color, including pale
and dark-on-dark text. It does not use the annotation toolbar's ink color.
Mixed colors within one source line use a dominant-color estimate.

SMALL-TEXT RECOGNITION
Small images use a bounded 2x recognition copy to help Windows OCR read short
glyphs and web addresses. Captured/exported pixels and size do not change.
OCR remains fallible: review URLs, part numbers and similar-looking characters.
No automatic spelling substitutions or guessed identifier rewrites are made.

CAPTURE TYPE
Your deliberate choice in the capture-type dropdown is saved immediately.
Closing an older snip or saving a Settings draft cannot overwrite that choice.
One-off full-screen menu actions do not change the saved startup capture type.
After updating, choose Rectangle (or your preferred type) once in the dropdown.
New windows will use that saved choice.

SMALL SNIPS
The editor reserves enough image-area height for small snips at 100% whenever
the monitor has room, including when automatic window resizing is turned off.
Larger snips still fit within the current monitor. Captured pixels are unchanged.

ZOOM
Type a zoom percentage in the footer and press Enter, or choose a preset such
as 100% from its dropdown. Escape cancels an edit without changing the view.
Fit remains available beside it. Zoom changes the preview, not exported pixels.

EYEDROPPER
Open a color palette and choose Eyedropper. Click a visible color anywhere on
the connected desktop, including your snip. The preview shows the exact RGB
and hex color. Escape or right-click cancels. Use color or Apply accepts the
palette's draft; sampling by itself does not change the snip or shape opacity.

The desktop is briefly frozen while choosing a color. That temporary image
stays in memory and is disposed afterward; it is not saved, copied, or added
to your snip history. Normal capture/taskbar policies are not invoked.

PRINT SCREEN SETUP
If Windows Snipping Tool opens first, use Set up Print Screen in Settings.
In Windows Settings > Accessibility > Keyboard, turn off the option that uses
Print Screen to open screen capture. Return to Snip Studio and choose Retry
Print Screen. Snip Studio shows On, Off, Windows default, or Unavailable instead
of assuming that registration guarantees priority over Windows.

Use Print Screen must also be enabled in Snip Studio, with an editor or its
shortcut listener running. Exit Snip Studio stops the listener. Start with
Windows remains optional. The program does not silently change Windows settings
or install a low-level keyboard hook.

INSTALL
Use the signed SnipStudio-0.6.21-Setup.exe. Save current work and approve Setup's
normal close prompt when requested. Existing app settings are retained.
The signed portable ZIP includes the same complete offline translation bundle,
models and third-party notices. See VALIDATION.txt for the verification scope.


