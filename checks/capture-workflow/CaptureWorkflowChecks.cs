using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio Capture Workflow Checks")]
[assembly: AssemblyDescription("Deterministic capture and recording workflow regression checks")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("0.6.22.0")]
[assembly: AssemblyFileVersion("0.6.22.0")]

internal static class CaptureWorkflowChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int assertions;
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); assertions++; }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Flags).GetValue(owner); }
    private static void Field(object owner, string name, object value)
    { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
    private static RecordingStatusEventArgs Status(Exception error)
    {
        return (RecordingStatusEventArgs)Activator.CreateInstance(typeof(RecordingStatusEventArgs), Flags,
            null, new object[] { true, TimeSpan.FromSeconds(10), "Synthetic recording status", error }, null);
    }
    private static void Deliver(MainForm form, object sender, RecordingStatusEventArgs status)
    { typeof(MainForm).GetMethod("OnRecordingStatus", Flags).Invoke(form, new object[] { sender, status }); }

    private static void SelectionInstructions()
    {
        Assembly app = typeof(MainForm).Assembly;
        Type overlayType = app.GetType("SnipStudio.CaptureOverlay", true);
        Type purposeType = app.GetType("SnipStudio.CaptureOverlayPurpose", true);
        Type listType = typeof(List<>).MakeGenericType(app.GetType("SnipStudio.CaptureWindow", true));
        using (Bitmap desktop = new Bitmap(640, 480))
        {
            foreach (string purpose in new string[] { "Screenshot", "Recording", "Scrolling" })
            {
                object windows = Activator.CreateInstance(listType);
                using (Form overlay = (Form)Activator.CreateInstance(overlayType, Flags, null,
                    new object[] { desktop, new Rectangle(-640, 0, 640, 480), CaptureMode.Rectangle,
                        windows, Enum.Parse(purposeType, purpose) }, null))
                {
                    string title = (string)Field(overlay, "instructionTitle");
                    string detail = (string)Field(overlay, "instructionDetail");
                    Check(overlay.Bounds == new Rectangle(-640, 0, 640, 480), purpose + ": selector preserves negative-monitor coordinates");
                    if (purpose == "Scrolling")
                    {
                        Check(title == "Select scrolling content", "Scrolling selector names the screenshot task");
                        Check(detail.IndexOf("content to scroll", StringComparison.Ordinal) >= 0, "Scrolling selector explains the intended selection");
                        Check(overlay.Text == "Choose scrolling content - Snip Studio", "Scrolling window title does not describe video recording");
                    }
                    else if (purpose == "Recording")
                    {
                        Check(title == "Drag to select a recording area", "Recording selector retains its recording instructions");
                        Check(overlay.Text == "Choose a recording area - Snip Studio", "Recording title remains unchanged");
                    }
                    else
                    {
                        Check(title == "Drag to select an area", "Screenshot selector retains its capture instructions");
                        Check(overlay.Text == "Choose a snip - Snip Studio", "Screenshot title remains unchanged");
                    }
                }
            }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = (MainForm)Activator.CreateInstance(typeof(MainForm), Flags,
                null, new object[] { null, true }, null))
            using (ScreenRecorder previous = new ScreenRecorder())
            using (ScreenRecorder current = new ScreenRecorder())
            {
                Field(form, "recordingSession", true);
                Field(form, "recordingStarting", true);
                Field(form, "screenRecorder", current);
                Exception staleError = new InvalidOperationException("Synthetic previous-session failure");
                Deliver(form, previous, Status(staleError));
                Check(Field(form, "recordingFailure") == null, "A queued previous-recorder error cannot poison the active recording");
                Check(Object.ReferenceEquals(Field(form, "screenRecorder"), current), "Previous status retains the active recorder");
                Check((bool)Field(form, "recordingSession"), "Previous status does not end the active session");
                Check(!(bool)Field(form, "stoppingRecording"), "Previous status does not begin stopping the active recording");

                Deliver(form, previous, Status(null));
                Check(Field(form, "recordingFailure") == null, "A previous normal notification cannot alter failure state");
                Deliver(form, null, Status(staleError));
                Check(Field(form, "recordingFailure") == null, "A notification without a recorder is ignored");

                Exception currentError = new InvalidOperationException("Synthetic current-session failure");
                Deliver(form, current, Status(currentError));
                Check(Object.ReferenceEquals(Field(form, "recordingFailure"), currentError), "The current recorder still reports its own error");
                Field(form, "recordingFailure", null);
                Field(form, "screenRecorder", null);
                Deliver(form, previous, Status(staleError));
                Check(Field(form, "recordingFailure") == null, "A completed recorder cannot report into a session without a current worker");
                Field(form, "recordingSession", false);
                Deliver(form, current, Status(currentError));
                Check(Field(form, "recordingFailure") == null, "Notifications outside a recording session remain ignored");
            }
            if (Array.IndexOf(args, "--status-only") < 0) SelectionInstructions();
            Console.WriteLine("PASS: " + assertions + " recording workflow assertions; deterministic notifications, no app entrypoint, capture, audio, file output or shown forms.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
