# Snip Studio 0.7.0

Snip Studio 0.7.0 brings the WinUI editor to the original Snip Studio release channel, with the Snip Studio name, launcher, and Windows shortcuts. Check for updates continues to use the original Snip Studio repository.

- Text Actions can reread fragmented text one row at a time. This helps preserve reading order, duplicate filenames, paths, and leading zeros. The text highlights are taller and more even across nearby rows. Difficult snips may take several seconds. Check recognized text against the image before using important identifiers or quantities.
- Delete and Undo refresh the image clipboard when automatic copying is enabled. A temporary clipboard lock is retried, and a newer copy takes precedence.
- The Snip Studio taskbar command **Close all without saving** asks once for the group. Individual closes still prompt when a snip has unsaved edits.
- The editor supports editable snip projects, word selection, redaction, and reviewed translations. Recording review includes a filmstrip, trimming, export, and copying.

## Install or upgrade

Use `SnipStudio-0.7.0-Setup.exe` on x64 Windows build 19041 or later. Save your open snips and finish recordings before upgrading. Follow Setup’s prompts to close the original installation. Snip Studio imports compatible preferences on first launch and keeps the old settings file. Review keyboard shortcuts in Settings, especially Print Screen. Use **Import previous settings** in Settings for any remaining choices. Keep any portable build’s files together and open `Snip Studio.exe`.

Check that Setup carries a valid timestamped Automation Forge signature. See the package validation notes for the tested upgrade path, signature receipts, and remaining verification.
