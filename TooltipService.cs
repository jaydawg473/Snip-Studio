using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    // All entry points are called on the application's UI thread. Providers
    // retain their own descriptions; this service fills only missing hints.
    internal static class TooltipService
    {
        private static bool enabled = true;
        private static bool started;
        private static readonly List<WeakReference> providers = new List<WeakReference>();
        private static readonly List<WeakReference> forms = new List<WeakReference>();
        private static readonly List<WeakReference> menus = new List<WeakReference>();
        private static int lastIdleScan;

        internal static bool Enabled { get { return enabled; } }

        internal static void SetEnabled(bool value)
        {
            if (started && enabled == value) return;
            enabled = value;
            StartWatching();
            ForEachProvider(delegate(ToolTip provider) { provider.Active = value; });
            ForEachForm(delegate(FormBinding binding) { binding.Refresh(); });
            ForEachMenu(delegate(MenuBinding binding) { binding.Refresh(); });
            ScanOpenForms();
        }

        internal static ToolTip RegisterProvider(ToolTip provider)
        {
            if (provider == null) return null;
            StartWatching();
            bool found = false;
            ForEachProvider(delegate(ToolTip existing) { if (Object.ReferenceEquals(existing, provider)) found = true; });
            if (!found)
            {
                providers.Add(new WeakReference(provider));
                provider.Disposed += OnProviderDisposed;
            }
            try { provider.Active = enabled; } catch (ObjectDisposedException) { return provider; }
            // A custom provider may have been registered after a form was bound.
            // Remove only the fallback hint, leaving the custom one untouched.
            if (!found) ForEachForm(delegate(FormBinding binding) { binding.Refresh(); });
            return provider;
        }

        internal static void Apply(Form form)
        {
            if (form == null || form.IsDisposed || form.Disposing) return;
            StartWatching();
            bool found = false;
            ForEachForm(delegate(FormBinding binding)
            {
                if (Object.ReferenceEquals(binding.Form, form)) { binding.Refresh(); found = true; }
            });
            if (!found) forms.Add(new WeakReference(new FormBinding(form)));
        }

        internal static void RegisterMenu(ToolStrip menu)
        {
            if (menu == null || menu.IsDisposed || menu.Disposing) return;
            StartWatching();
            bool found = false;
            ForEachMenu(delegate(MenuBinding binding)
            {
                if (Object.ReferenceEquals(binding.Menu, menu)) { binding.Refresh(); found = true; }
            });
            if (!found) menus.Add(new WeakReference(new MenuBinding(menu)));
        }

        private static void StartWatching()
        {
            if (started) return;
            started = true;
            Application.Idle += OnIdle;
            Application.ApplicationExit += OnApplicationExit;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            if (unchecked(now - lastIdleScan) < 300 && lastIdleScan != 0) return;
            lastIdleScan = now;
            ScanOpenForms();
        }

        private static void ScanOpenForms()
        {
            List<Form> open = new List<Form>();
            foreach (Form form in Application.OpenForms) open.Add(form);
            foreach (Form form in open)
            {
                bool known = false;
                ForEachForm(delegate(FormBinding binding) { if (Object.ReferenceEquals(binding.Form, form)) known = true; });
                if (!known) Apply(form);
            }
        }

        private static void OnApplicationExit(object sender, EventArgs e)
        {
            Application.Idle -= OnIdle;
            Application.ApplicationExit -= OnApplicationExit;
            ForEachForm(delegate(FormBinding binding) { binding.Dispose(); });
            ForEachMenu(delegate(MenuBinding binding) { binding.Dispose(); });
            forms.Clear(); menus.Clear(); providers.Clear();
            started = false;
        }

        private static void ForEachProvider(Action<ToolTip> action)
        {
            for (int i = providers.Count - 1; i >= 0; i--)
            {
                ToolTip provider = providers[i].Target as ToolTip;
                if (provider == null) { providers.RemoveAt(i); continue; }
                try { action(provider); }
                catch (ObjectDisposedException) { providers.RemoveAt(i); }
            }
        }

        private static void OnProviderDisposed(object sender, EventArgs e)
        {
            for (int i = providers.Count - 1; i >= 0; i--)
                if (!providers[i].IsAlive || Object.ReferenceEquals(providers[i].Target, sender)) providers.RemoveAt(i);
        }

        private static void ForEachForm(Action<FormBinding> action)
        {
            for (int i = forms.Count - 1; i >= 0; i--)
            {
                FormBinding binding = forms[i].Target as FormBinding;
                if (binding == null || binding.Disposed) { forms.RemoveAt(i); continue; }
                action(binding);
            }
        }

        private static void ForEachMenu(Action<MenuBinding> action)
        {
            for (int i = menus.Count - 1; i >= 0; i--)
            {
                MenuBinding binding = menus[i].Target as MenuBinding;
                if (binding == null || binding.Disposed) { menus.RemoveAt(i); continue; }
                action(binding);
            }
        }

        private static string ExistingHint(Control control)
        {
            string hint = null;
            ForEachProvider(delegate(ToolTip provider)
            {
                if (!String.IsNullOrWhiteSpace(hint)) return;
                hint = provider.GetToolTip(control);
                if (String.IsNullOrWhiteSpace(hint)) return;
                // Some older icon buttons supplied only their caption. Turn
                // those into explanations while retaining any custom prose.
                string label = control.AccessibleName;
                if (String.IsNullOrWhiteSpace(label) && control is ButtonBase) label = control.Text;
                if (!String.IsNullOrWhiteSpace(label) && String.Equals(CleanLabel(hint), CleanLabel(label), StringComparison.OrdinalIgnoreCase))
                {
                    string explanation = DescribeLabel(label);
                    if (!String.IsNullOrWhiteSpace(explanation) && !String.Equals(hint, explanation, StringComparison.Ordinal))
                    { provider.SetToolTip(control, explanation); hint = explanation; }
                }
            });
            return hint;
        }

        private sealed class FormBinding : IDisposable
        {
            internal readonly Form Form;
            private readonly ToolTip fallback;
            private readonly HashSet<Control> watched = new HashSet<Control>();
            private Control disabledHover;
            private Control disabledHoverOwner;
            internal bool Disposed { get; private set; }

            internal FormBinding(Form form)
            {
                Form = form;
                fallback = new ToolTip { Active = enabled, ShowAlways = true, InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000 };
                form.Disposed += OnFormDisposed;
                Watch(form);
            }

            private void Watch(Control control)
            {
                if (control == null || control.IsDisposed || !watched.Add(control)) return;
                control.ControlAdded += OnControlAdded;
                control.ControlRemoved += OnControlRemoved;
                control.TextChanged += OnControlChanged;
                control.EnabledChanged += OnControlChanged;
                control.MouseMove += OnMouseMove;
                control.MouseLeave += OnMouseLeave;
                control.Disposed += OnControlDisposed;
                UpdateHint(control);
                ToolStrip strip = control as ToolStrip;
                if (strip != null) RegisterMenu(strip);
                if (control.ContextMenuStrip != null) RegisterMenu(control.ContextMenuStrip);
                foreach (Control child in control.Controls) Watch(child);
            }

            private void Unwatch(Control control)
            {
                if (control == null || !watched.Remove(control)) return;
                foreach (Control child in control.Controls) Unwatch(child);
                control.ControlAdded -= OnControlAdded;
                control.ControlRemoved -= OnControlRemoved;
                control.TextChanged -= OnControlChanged;
                control.EnabledChanged -= OnControlChanged;
                control.MouseMove -= OnMouseMove;
                control.MouseLeave -= OnMouseLeave;
                control.Disposed -= OnControlDisposed;
                if (!control.IsDisposed) fallback.SetToolTip(control, null);
            }

            internal void Refresh()
            {
                if (Disposed || Form.IsDisposed) return;
                fallback.Active = enabled;
                if (!enabled) HideDisabledHint();
                foreach (Control control in new List<Control>(watched))
                {
                    if (control.IsDisposed) { watched.Remove(control); continue; }
                    UpdateHint(control);
                    if (control.ContextMenuStrip != null) RegisterMenu(control.ContextMenuStrip);
                }
            }

            private void UpdateHint(Control control)
            {
                if (Disposed || control.IsDisposed) return;
                string custom = ExistingHint(control);
                string desired = String.IsNullOrWhiteSpace(custom) ? Describe(control) : null;
                if (!String.Equals(fallback.GetToolTip(control), desired ?? "", StringComparison.Ordinal)) fallback.SetToolTip(control, desired);
            }

            private void OnControlAdded(object sender, ControlEventArgs e) { Watch(e.Control); }
            private void OnControlRemoved(object sender, ControlEventArgs e) { Unwatch(e.Control); }
            private void OnControlChanged(object sender, EventArgs e) { UpdateHint((Control)sender); }
            private void OnControlDisposed(object sender, EventArgs e) { Unwatch((Control)sender); }
            private void OnFormDisposed(object sender, EventArgs e) { Dispose(); }

            private void OnMouseMove(object sender, MouseEventArgs e)
            {
                if (!enabled || Disposed) return;
                Control parent = sender as Control;
                if (parent == null || parent.IsDisposed) return;
                Control child = parent.GetChildAtPoint(e.Location, GetChildAtPointSkip.Invisible);
                Point screen = parent.PointToScreen(e.Location);
                while (child != null && child.HasChildren && !IsInteractive(child))
                {
                    Control nested = child.GetChildAtPoint(child.PointToClient(screen), GetChildAtPointSkip.Invisible);
                    if (nested == null) break;
                    child = nested;
                }
                // Native tooltips do not receive hover messages from disabled
                // controls. Their parent can explain the unavailable action.
                if (child != null && !child.Enabled && IsInteractive(child))
                {
                    if (Object.ReferenceEquals(disabledHover, child)) return;
                    string hint = ExistingHint(child);
                    if (String.IsNullOrWhiteSpace(hint)) hint = Describe(child);
                    if (!String.IsNullOrWhiteSpace(hint))
                    {
                        disabledHover = child;
                        disabledHoverOwner = parent;
                        fallback.Show(hint, parent, e.X + 12, e.Y + 20, 6000);
                        return;
                    }
                }
                if (disabledHover != null) HideDisabledHint();
            }

            private void OnMouseLeave(object sender, EventArgs e)
            {
                if (disabledHover == null || Disposed) return;
                HideDisabledHint();
            }

            private void HideDisabledHint()
            {
                if (disabledHoverOwner != null && !disabledHoverOwner.IsDisposed) fallback.Hide(disabledHoverOwner);
                disabledHover = null;
                disabledHoverOwner = null;
            }

            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Form.Disposed -= OnFormDisposed;
                foreach (Control control in new List<Control>(watched)) Unwatch(control);
                watched.Clear();
                fallback.Dispose();
            }
        }

        private sealed class MenuBinding : IDisposable
        {
            internal readonly ToolStrip Menu;
            private readonly Dictionary<ToolStripItem, string> generated = new Dictionary<ToolStripItem, string>();
            private readonly HashSet<ToolStripItem> watched = new HashSet<ToolStripItem>();
            internal bool Disposed { get; private set; }

            internal MenuBinding(ToolStrip menu)
            {
                Menu = menu;
                menu.Disposed += OnMenuDisposed;
                menu.ItemAdded += OnItemAdded;
                menu.ItemRemoved += OnItemRemoved;
                Refresh();
            }

            internal void Refresh()
            {
                if (Disposed || Menu.IsDisposed) return;
                Menu.ShowItemToolTips = enabled;
                foreach (ToolStripItem item in Menu.Items) Watch(item);
            }

            private void Watch(ToolStripItem item)
            {
                if (item == null || item.IsDisposed || item is ToolStripSeparator) return;
                if (watched.Add(item))
                {
                    item.TextChanged += OnItemChanged;
                    item.Disposed += OnItemDisposed;
                    ToolStripDropDownItem drop = item as ToolStripDropDownItem;
                    if (drop != null) drop.DropDownOpening += OnDropDownOpening;
                }
                string previous;
                if (String.IsNullOrWhiteSpace(item.ToolTipText) || item.ToolTipText == item.Text ||
                    (generated.TryGetValue(item, out previous) && item.ToolTipText == previous))
                {
                    string label = FirstText(item.AccessibleName, item.Text);
                    string hint = FirstText(item.AccessibleDescription, DescribeLabel(label));
                    if (String.IsNullOrWhiteSpace(hint) && !String.IsNullOrWhiteSpace(label)) hint = "Choose " + CleanLabel(label) + ".";
                    item.ToolTipText = hint;
                    generated[item] = hint;
                }
                ToolStripDropDownItem dropdown = item as ToolStripDropDownItem;
                if (dropdown != null && dropdown.HasDropDownItems) RegisterMenu(dropdown.DropDown);
            }

            private void Unwatch(ToolStripItem item)
            {
                if (item == null || !watched.Remove(item)) return;
                item.TextChanged -= OnItemChanged;
                item.Disposed -= OnItemDisposed;
                ToolStripDropDownItem dropdown = item as ToolStripDropDownItem;
                if (dropdown != null) dropdown.DropDownOpening -= OnDropDownOpening;
                generated.Remove(item);
            }

            private void OnItemAdded(object sender, ToolStripItemEventArgs e) { Watch(e.Item); }
            private void OnItemRemoved(object sender, ToolStripItemEventArgs e) { Unwatch(e.Item); }
            private void OnItemChanged(object sender, EventArgs e) { Watch((ToolStripItem)sender); }
            private void OnItemDisposed(object sender, EventArgs e) { Unwatch((ToolStripItem)sender); }
            private void OnDropDownOpening(object sender, EventArgs e)
            {
                ToolStripDropDownItem item = sender as ToolStripDropDownItem;
                if (item != null) RegisterMenu(item.DropDown);
            }
            private void OnMenuDisposed(object sender, EventArgs e) { Dispose(); }
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Menu.Disposed -= OnMenuDisposed;
                Menu.ItemAdded -= OnItemAdded;
                Menu.ItemRemoved -= OnItemRemoved;
                foreach (ToolStripItem item in new List<ToolStripItem>(watched)) Unwatch(item);
            }
        }

        private static bool IsInteractive(Control control)
        {
            TextBoxBase text = control as TextBoxBase;
            // Do not cover the canvas or the user's multiline editing surface.
            if (text != null && text.Multiline) return false;
            return control is ButtonBase || control is ComboBox || control is NumericUpDown ||
                text != null || control is TrackBar || control is LinkLabel ||
                (control.TabStop && !String.IsNullOrWhiteSpace(control.AccessibleName) && !String.IsNullOrWhiteSpace(control.AccessibleDescription));
        }

        private static string Describe(Control control)
        {
            if (!IsInteractive(control)) return null;
            string description = control.AccessibleDescription;
            if (!String.IsNullOrWhiteSpace(description)) return description;
            string label = control.AccessibleName;
            // Button and toggle captions are static action labels. Input values
            // can contain private filenames or text and are never used as hints.
            if (String.IsNullOrWhiteSpace(label) && (control is ButtonBase || control is LinkLabel)) label = control.Text;
            string known = DescribeLabel(label);
            if (!String.IsNullOrWhiteSpace(known)) return known;
            if (String.IsNullOrWhiteSpace(label)) label = NearbyLabel(control);
            if (String.IsNullOrWhiteSpace(label))
            {
                if (control is ComboBox) return "Open the list to choose an option.";
                if (control is NumericUpDown) return "Type a value or use the arrows to adjust it.";
                if (control is TextBoxBase) return "Enter or edit this value.";
                return null;
            }
            label = CleanLabel(label);
            if (control is CheckBox) return "Turn " + label + " on or off.";
            if (control is RadioButton || control is ComboBox) return "Choose " + label + ".";
            if (control is NumericUpDown) return "Set " + label + ". Type a value or use the arrows.";
            if (control is TextBoxBase) return ((TextBoxBase)control).ReadOnly ? "View " + label + "." : "Enter or edit " + label + ".";
            return label.EndsWith(".", StringComparison.Ordinal) ? label : label + ".";
        }

        private static string NearbyLabel(Control control)
        {
            if (control.Parent == null) return null;
            Label nearest = null;
            int distance = Int32.MaxValue;
            foreach (Control sibling in control.Parent.Controls)
            {
                Label label = sibling as Label;
                if (label == null || label is LinkLabel || String.IsNullOrWhiteSpace(label.Text)) continue;
                int vertical = Math.Abs(label.Top - control.Top);
                int candidate = label.Right <= control.Left + 8 && vertical < Math.Max(control.Height, label.Height)
                    ? Math.Max(0, control.Left - label.Right) + vertical
                    : label.Bottom <= control.Top + 2 ? control.Top - label.Bottom + 100 : Int32.MaxValue;
                if (candidate < distance && candidate < 180) { nearest = label; distance = candidate; }
            }
            return nearest == null ? null : nearest.Text;
        }

        private static string DescribeLabel(string label)
        {
            string key = CleanLabel(label).ToLowerInvariant();
            if (key.Length == 0) return null;
            if (key.StartsWith("capture delay")) return "Choose how long to wait before taking a screenshot. Off captures immediately.";
            if (key == "new" || key == "new screenshot" || key == "new capture") return "Start a new capture in the selected screenshot or video mode. Ctrl+N.";
            if (key == "screenshot mode" || key == "photo" || key == "photo mode") return "Switch to screenshots. Choose New to capture an image.";
            if (key == "record mode" || key == "video" || key == "video mode") return "Switch to video. Choose Record to select an area and start recording.";
            if (key == "record" || key == "record screen" || key == "start recording") return "Select an area to record, then start a video with your chosen audio sources.";
            if (key.StartsWith("stop recording") || key == "stop") return "Stop recording and finish saving the MP4 video.";
            if (key == "capture mode") return "Choose a rectangle, freeform area, window, or the full desktop. Regions can span monitors.";
            if (key == "rectangle") return "Drag a rectangular screenshot area.";
            if (key == "freeform") return "Draw an outline around the screenshot area you want to keep.";
            if (key == "window") return "Capture the visible area of a window you choose.";
            if (key == "full screen" || key == "fullscreen") return "Capture the full desktop across connected monitors.";
            if (key == "no delay") return "Take the screenshot without a delay.";
            if (key.EndsWith(" seconds", StringComparison.Ordinal)) return "Wait " + key + " before taking the screenshot.";
            if (key == "audio options") return "Choose whether to record a microphone, system sound, or both, and select their devices.";
            if (key.Contains("microphone")) return "Choose the microphone input used for recordings, or follow the Windows default.";
            if (key.Contains("system audio") || key.Contains("playback device")) return "Choose the speaker or output device whose sound is recorded, or follow the Windows default.";
            if (key == "move") return "Select an annotation to move it. Drag text side handles to wrap it, or arrow endpoints to rotate it.";
            if (key == "pen") return "Draw freehand marks. Use Move to reposition them afterward.";
            if (key == "highlight") return "Highlight recognized text, or hold Shift while dragging for a freehand mark.";
            if (key == "arrow") return "Drag from the tail to the arrowhead. Use Move and drag either endpoint to rotate or resize it.";
            if (key == "box") return "Draw a rectangle around part of the image.";
            if (key == "add text") return "Add a note with a chosen font size and wrapping width. Double-click an existing note to edit it.";
            if (key == "eraser") return "Remove annotations without erasing the original screenshot pixels.";
            if (key == "crop") return "Drag to keep part of the image. Undo restores the previous crop.";
            if (key == "ink color" || key == "text color") return "Choose a color from the palette, recent colors, or an exact hex value.";
            if (key == "background color") return "Choose the fill that covers the old screenshot text before the replacement is drawn.";
            if (key == "stroke width") return "Choose the thickness of new pen, arrow, box, or highlight marks.";
            if (key == "text size" || key.Contains("font size")) return "Choose text size in image pixels. Applies to the selected text note or new text.";
            if (key.Contains("box width") || key.Contains("text width")) return "Set the text box width. Narrower boxes wrap onto more lines without shrinking the font.";
            if (key == "snap to text") return "Align highlighting with recognized words and lines. Hold Shift to draw freely.";
            if (key == "text actions" || key == "copy text") return "Recognize text on the image so you can select, copy, edit, or delete words.";
            if (key == "edit line") return "Edit a recognized line. Select words from one line first, then replace or remove text.";
            if (key == "delete selection") return "Remove the selected recognized words. Undo restores the previous image.";
            if (key == "copy all text") return "Copy all recognized text to the clipboard, preserving line breaks.";
            if (key == "copy selection") return "Copy the words currently selected on the image to the clipboard.";
            if (key == "done") return "Close text actions and return to image editing.";
            if (key == "undo") return "Undo the last edit. Ctrl+Z.";
            if (key == "redo") return "Restore an edit that you undid. Ctrl+Y.";
            if (key == "open image" || key.StartsWith("open file")) return "Open an image from this computer for viewing or annotation. Ctrl+O.";
            if (key == "paste image") return "Open an image from the clipboard. Ctrl+V.";
            if (key == "copy image") return "Copy the snip and its annotations to the clipboard at the image's full resolution. Ctrl+C.";
            if (key.StartsWith("save as pdf")) return "Save the snip and its annotations as a PDF document.";
            if (key.StartsWith("save as")) return "Save a copy with a different filename, folder, or supported format.";
            if (key == "save image") return "Save the snip and its annotations. Use Save as to choose another image format or PDF. Ctrl+S.";
            if (key == "save") return "Save the current changes.";
            if (key.StartsWith("print")) return "Preview the snip, choose a printer and page options, then confirm printing. Ctrl+P.";
            if (key.StartsWith("share")) return "Open Windows Share for the current file, then choose where to send it.";
            if (key.StartsWith("reload external edit")) return "Load the working copy you saved in your external editor back into Snip Studio.";
            if (key.StartsWith("edit in")) return "Open a separate working copy in your chosen image editor. Save there, then use Reload external edit.";
            if (key.StartsWith("open screenshots folder")) return "Open the folder used for automatically saved original screenshots.";
            if (key.StartsWith("open recordings folder")) return "Open the folder used for automatically saved recordings.";
            if (key.StartsWith("review a recording")) return "Open a recording to play, copy, trim, or create a smaller MP4.";
            if (key == "more" || key == "more actions") return "Open file actions, sharing, printing, settings, help, and app information.";
            if (key == "zoom in") return "Enlarge the preview. The saved image resolution stays unchanged.";
            if (key == "zoom out") return "Reduce the preview size. The saved image resolution stays unchanged.";
            if (key == "fit" || key.StartsWith("fit to window")) return "Fit the entire snip into the available preview area.";
            if (key.StartsWith("actual size")) return "Show one screenshot pixel per preview pixel at 100% zoom.";
            if (key == "settings") return "Choose capture, recording, saving, appearance, shortcut, and tooltip preferences.";
            if (key == "help") return "Open instructions for capturing, editing, saving, and recording. F1.";
            if (key.StartsWith("about")) return "Show the app version, Automation Forge information, and website.";
            if (key.StartsWith("exit snip studio")) return "Exit Snip Studio. Unsaved edits are checked before its windows close.";
            if (key == "cancel") return "Close this action without applying its pending changes.";
            if (key == "apply") return "Apply the text and formatting changes to the snip. Undo can restore the previous state.";
            if (key.StartsWith("use selected color") || key == "use color") return "Apply the color shown in the preview.";
            if (key == "hex color") return "Enter an RGB hex color such as #0078D4. Three-digit shorthand is also accepted.";
            if (key.StartsWith("make smaller") || key.StartsWith("email copy") || key == "recording export quality") return "Choose a smaller-video preset or target size and save a separate MP4. The original recording is preserved.";
            if (key == "create copy") return "Encode a separate video using the selected quality or target file size.";
            if (key.Contains("target video file size") || key.Contains("target file size")) return "Choose the approximate video size in megabytes. Leave room below your email attachment limit.";
            if (key.StartsWith("play video") || key == "play") return "Open this recording in your default video application.";
            if (key.StartsWith("copy video") || key == "copy file") return "Copy the video file to paste into a folder, email, or chat that accepts files.";
            if (key.Contains("trim start") || key.Contains("start time")) return "Set where the trimmed copy begins, in seconds.";
            if (key.Contains("trim end") || key.Contains("end time")) return "Set where the trimmed copy ends, in seconds.";
            if (key.StartsWith("apply trim") || key == "trim") return "Create a new MP4 containing the selected time range. The original stays unchanged.";
            if (key.StartsWith("open folder")) return "Open this folder in File Explorer.";
            if (key.StartsWith("choose app")) return "Choose the image editor to open from Snip Studio.";
            if (key.Contains("detected image editing")) return "Choose an installed image editor. Refresh the list after installing an app.";
            if (key == "refresh") return "Refresh the available devices or installed applications in this section.";
            if (key == "app theme") return "Choose Light, Dark, or the current Windows appearance.";
            if (key == "show tooltips") return "Show short explanations when you pause over controls. Turn off to hide these hints.";
            return null;
        }

        private static string CleanLabel(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            text = text.Replace("&", "").Replace("…", "").Trim();
            int shortcut = text.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase);
            if (shortcut >= 0) text = text.Substring(0, shortcut).Trim();
            return text.TrimEnd('.', ':', ' ');
        }

        private static string FirstText(string first, string second)
        { return String.IsNullOrWhiteSpace(first) ? second : first; }
    }
}
