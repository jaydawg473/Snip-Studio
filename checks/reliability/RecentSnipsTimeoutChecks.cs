using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Snip Studio Recent Snips Checks")]
[assembly: AssemblyDescription("Synthetic timeout, stale-result and bitmap ownership checks")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyVersion("0.6.22.0")]
[assembly: AssemblyFileVersion("0.6.22.0")]

internal static class RecentSnipsTimeoutChecks
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly app;
    private static Type formType, resultType;
    private static int assertions, opens;

    private sealed class Providers
    {
        internal object Recent;
        internal CancellationToken RecentToken, PreviewToken;
        internal Task<Bitmap> Preview = Task.FromResult<Bitmap>(null);
        internal Task<Bitmap> Thumbnail = Task.FromResult<Bitmap>(null);
        internal int Reads;
        public object Read(CancellationToken token) { Reads++; RecentToken = token; return Recent; }
        internal Task<Bitmap> ReadPreview(string path, int edge, CancellationToken token)
        {
            if (edge == 128) return Thumbnail;
            PreviewToken = token;
            return Preview;
        }
    }

    private static object Field(object owner, string name) { return owner.GetType().GetField(name, Any).GetValue(owner); }
    private static void SetField(object owner, string name, object value) { owner.GetType().GetField(name, Any).SetValue(owner, value); }
    private static object Call(object owner, string name, params object[] values) { return owner.GetType().GetMethod(name, Any).Invoke(owner, values); }
    private static Task Refresh(Form form) { return (Task)Call(form, "RefreshRecentSnipsAsync"); }
    private static Task Preview(Form form, int index) { return (Task)Call(form, "PreviewItemAsync", index); }
    private static Control Control(Form form, string name) { return (Control)Field(form, name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); assertions++; }

    private static object Result(params string[] names)
    {
        string[] paths = new string[names.Length];
        for (int index = 0; index < names.Length; index++) paths[index] = "C:\\SyntheticRecentSnips\\" + names[index] + ".png";
        Type service = app.GetType("SnipStudio.RecentSnipService", true);
        return service.GetMethod("CreateRecentResult", Any).Invoke(null, new object[] { paths, 32, CancellationToken.None });
    }

    private static object Completion()
    { return Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType)); }
    private static object CompletionTask(object completion) { return completion.GetType().GetProperty("Task").GetValue(completion, null); }
    private static void Complete(object completion, object result) { completion.GetType().GetMethod("SetResult").Invoke(completion, new object[] { result }); }
    private static object Ready(object result) { object completion = Completion(); Complete(completion, result); return CompletionTask(completion); }

    private static Form NewForm(Providers providers)
    {
        ConstructorInfo constructor = null;
        foreach (ConstructorInfo item in formType.GetConstructors(Any)) if (item.GetParameters().Length == 4) constructor = item;
        if (constructor == null) throw new InvalidOperationException("Bounded request fixture constructor is missing.");
        Type providerType = constructor.GetParameters()[0].ParameterType;
        Type taskType = providerType.GetGenericArguments()[1];
        ParameterExpression token = Expression.Parameter(typeof(CancellationToken), "token");
        Expression read = Expression.Convert(Expression.Call(Expression.Constant(providers), typeof(Providers).GetMethod("Read"), token), taskType);
        Delegate recent = Expression.Lambda(providerType, read, token).Compile();
        Func<string, int, CancellationToken, Task<Bitmap>> preview = providers.ReadPreview;
        Action<string> open = delegate { opens++; };
        return (Form)constructor.Invoke(new object[] { recent, preview, open, 60 });
    }

    private static void Pump(Func<bool> condition, string label)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 2500) { Application.DoEvents(); Thread.Sleep(1); }
        Check(condition(), label);
    }
    private static void Finish(Task task, string label) { Pump(delegate { return task.IsCompleted; }, label); task.GetAwaiter().GetResult(); }
    private static Bitmap CurrentPreview(Form form) { return (Bitmap)Field(Field(form, "preview"), "Image"); }
    private static string PreviewMessage(Form form) { return (string)Field(Field(form, "preview"), "Message"); }
    private static bool Disposed(Bitmap image) { try { int width = image.Width; return false; } catch (ArgumentException) { return true; } }
    private static int Count(Form form) { return ((IList)Field(form, "rows")).Count; }

    private static void ListTimeoutKeepsState()
    {
        Providers providers = new Providers { Recent = Ready(Result("First", "Second")) };
        using (Form form = NewForm(providers))
        {
            Check(!form.Visible && providers.Reads == 0, "constructing gallery does not read real history");
            Finish(Refresh(form), "initial synthetic list completes");
            Check(Count(form) == 2, "initial two rows are present");
            Finish(Preview(form, 1), "select second row");
            string selectedPath = Control(form, "path").Text;
            object pending = Completion();
            providers.Recent = CompletionTask(pending);
            Task refresh = Refresh(form);
            Check(!Control(form, "refresh").Enabled, "refresh indicates pending state");
            Check(Count(form) == 2 && Control(form, "path").Text == selectedPath, "pending refresh keeps prior rows and selection");
            Finish(refresh, "hung provider releases UI after bounded wait");
            Check(Control(form, "refresh").Enabled, "timed-out refresh becomes available again");
            Check(Control(form, "status").Text.Contains("took too long"), "timeout is explained rather than remaining loading");
            Check(Count(form) == 2 && Control(form, "path").Text == selectedPath, "timeout preserves previous rows and selected path");
            Check(providers.RecentToken.IsCancellationRequested, "timeout cancels backend request");
            Complete(pending, Result("Late"));
            Application.DoEvents();
            Check(Count(form) == 2 && Control(form, "path").Text == selectedPath, "late list does not replace retained rows");
            providers.Recent = Ready(Result("Second", "First"));
            Finish(Refresh(form), "retry succeeds");
            Check(Count(form) == 2 && ((ListBox)Control(form, "recentList")).SelectedIndex == 0, "successful refresh keeps selected path at its new index");
            Check(Control(form, "path").Text == selectedPath, "successful refresh preserves logical selection");
            object valid = Result("Ignored");
            object items = resultType.GetProperty("Items", Any).GetValue(valid, null);
            object unavailable = Activator.CreateInstance(resultType, Any, null, new object[] { items, "Windows unavailable.", false }, null);
            providers.Recent = Ready(unavailable);
            Finish(Refresh(form), "unavailable list completes");
            Check(Count(form) == 2 && Control(form, "path").Text == selectedPath, "unavailable refresh also keeps prior usable list");
        }
    }

    private static void PreviewTimeoutAndLateOwnership()
    {
        Providers providers = new Providers { Recent = Ready(Result("First", "Second")) };
        Bitmap blue = new Bitmap(3, 3);
        blue.SetPixel(0, 0, Color.Blue);
        using (Form form = NewForm(providers))
        {
            Finish(Refresh(form), "preview fixture list completes");
            ListBox list = (ListBox)Control(form, "recentList");
            list.SelectedIndex = 1;
            TaskCompletionSource<Bitmap> pending = new TaskCompletionSource<Bitmap>();
            providers.Preview = pending.Task;
            Task timeout = Preview(form, 0);
            list.SelectedIndex = 0;
            Finish(timeout, "hung large preview releases UI after bounded wait");
            Check(providers.PreviewToken.IsCancellationRequested, "preview timeout cancels backend request");
            Check(PreviewMessage(form).Contains("too long"), "preview timeout has honest unavailable state");
            Check(Control(form, "open").Enabled && Field(form, "previewCancellation") == null, "Open remains available and same-row preview may retry");
            Bitmap late = new Bitmap(4, 4);
            pending.SetResult(late);
            Pump(delegate { return Disposed(late); }, "late ignored bitmap is disposed");
            Check(CurrentPreview(form) == null, "timed-out bitmap never replaces preview");
            providers.Preview = Task.FromResult(blue);
            IntPtr handle = list.Handle;
            Rectangle row = list.GetItemRectangle(0);
            Call(list, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, row.Left + 8, row.Top + Math.Max(1, row.Height / 2), 0));
            Pump(delegate { return Object.ReferenceEquals(CurrentPreview(form), blue); }, "clicking the already-selected row retries its preview");
            Check(Object.ReferenceEquals(CurrentPreview(form), blue) && CurrentPreview(form).GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(), "successful preview retains bitmap ownership");
        }
        Check(Disposed(blue), "gallery disposal releases displayed bitmap");
    }

    private static void RetainedThumbnailsSettle()
    {
        TaskCompletionSource<Bitmap> thumbnail = new TaskCompletionSource<Bitmap>();
        Providers providers = new Providers { Recent = Ready(Result("First", "Second")), Thumbnail = thumbnail.Task };
        using (Form form = NewForm(providers))
        {
            Finish(Refresh(form), "pending-thumbnail fixture list completes");
            IList rows = (IList)Field(form, "rows");
            Check(!(bool)Field(rows[0], "ThumbnailLoaded"), "initial thumbnail really is pending");
            object pending = Completion();
            providers.Recent = CompletionTask(pending);
            Finish(Refresh(form), "refresh timeout retains pending-thumbnail rows");
            foreach (object row in rows) Check((bool)Field(row, "ThumbnailLoaded"), "retained row no longer claims a canceled thumbnail is loading");
            Bitmap late = new Bitmap(3, 3);
            thumbnail.SetResult(late);
            Pump(delegate { return Disposed(late); }, "canceled retained-row thumbnail result is disposed");
            Check(Count(form) == 2, "late row thumbnail does not remove retained rows");
            Complete(pending, Result("Late"));
        }
    }

    private static void SupersededAndDisposedRequests()
    {
        Providers providers = new Providers { Recent = Ready(Result("First", "Second")) };
        Form form = NewForm(providers);
        Finish(Refresh(form), "stale fixture list completes");
        TaskCompletionSource<Bitmap> older = new TaskCompletionSource<Bitmap>();
        providers.Preview = older.Task;
        Task stale = Preview(form, 1);
        Bitmap current = new Bitmap(3, 3);
        current.SetPixel(0, 0, Color.Yellow);
        providers.Preview = Task.FromResult(current);
        Finish(Preview(form, 0), "newer preview supersedes a non-cooperating provider");
        Finish(stale, "superseded preview finishes through cancellation");
        Bitmap late = new Bitmap(3, 3);
        older.SetResult(late);
        Pump(delegate { return Disposed(late); }, "superseded late bitmap is disposed");
        Check(Object.ReferenceEquals(CurrentPreview(form), current), "late stale preview does not replace current selection");
        TaskCompletionSource<Bitmap> closing = new TaskCompletionSource<Bitmap>();
        providers.Preview = closing.Task;
        Task duringClose = Preview(form, 1);
        form.Dispose();
        Finish(duringClose, "closing gallery cancels pending preview immediately");
        Bitmap afterClose = new Bitmap(3, 3);
        closing.SetResult(afterClose);
        Pump(delegate { return Disposed(afterClose); }, "bitmap arriving after closure is disposed");
        Check(opens == 0, "refresh, preview, timeout and disposal never open an editor");
    }

    private static void ReachableActions(Form form, string label)
    {
        Control[] actions = { Control(form, "refresh"), Control(form, "open"), Control(form, "close") };
        foreach (Control action in actions)
        {
            Check(action.Width > 0 && action.Height > 0, label + ": action has usable bounds");
            Check(form.ClientRectangle.Contains(action.Bounds), label + ": action is inside the actual client area");
            foreach (string content in new string[] { "recentList", "preview", "path", "help", "status" })
                Check(!action.Bounds.IntersectsWith(Control(form, content).Bounds), label + ": action does not cover " + content);
        }
        for (int first = 0; first < actions.Length; first++)
            for (int second = first + 1; second < actions.Length; second++)
                Check(!actions[first].Bounds.IntersectsWith(actions[second].Bounds), label + ": action buttons remain separate");
    }

    private static void ResponsiveFooter()
    {
        int cases = 0;
        foreach (float scale in new float[] { 1f, 1.25f, 1.5f, 2f })
        foreach (Size client in new Size[] { new Size(940, 650), new Size(616, 650), new Size(400, 700), new Size(320, 760),
            new Size(616, 420), new Size(400, 480), new Size(320, 480) })
        {
            Providers providers = new Providers { Recent = Ready(Result()) };
            using (Form form = NewForm(providers))
            {
                SetField(form, "LayoutScaleOverride", scale);
                form.MinimumSize = Size.Empty;
                form.ClientSize = client;
                form.PerformLayout();
                ReachableActions(form, "client " + client + " at " + scale);
                if (scale == 2f && client.Width == 616)
                    Check(Control(form, "refresh").Top < Control(form, "open").Top,
                        "616px at 200 percent uses two action rows rather than overlapping");
                if (scale == 2f && client.Width == 320)
                    Check(Control(form, "refresh").Top < Control(form, "open").Top && Control(form, "open").Top < Control(form, "close").Top,
                        "narrow 200 percent actions stack in reading order");
                cases++;
            }
        }
        foreach (float scale in new float[] { 1f, 1.5f, 2f })
        foreach (Rectangle work in new Rectangle[] { new Rectangle(0, 0, 640, 800), new Rectangle(0, 0, 800, 800),
            new Rectangle(0, 0, 640, 480), new Rectangle(0, 0, 800, 480), new Rectangle(0, 0, 640, 600), new Rectangle(0, 0, 800, 600) })
        {
            Providers providers = new Providers { Recent = Ready(Result()) };
            using (Form form = NewForm(providers))
            {
                SetField(form, "LayoutScaleOverride", scale);
                SetField(form, "LayoutWorkingAreaOverride", work);
                form.Size = new Size(1400, 1000);
                Call(form, "OnShown", EventArgs.Empty);
                form.PerformLayout();
                Check(form.Width <= work.Width - 24 && form.Height <= work.Height - 24,
                    "production OnShown clamp uses the available working area");
                ReachableActions(form, "working-area clamp " + work.Size + " at " + scale);
                cases++;
            }
        }
        Console.WriteLine("Layout cases: " + cases + "; actual controls, working-area clamp and logical DPI overrides.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Supply the exact signed application path.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            app = Assembly.LoadFrom(args[0]);
            formType = app.GetType("SnipStudio.RecentSnipsForm", true);
            resultType = app.GetType("SnipStudio.RecentSnipsResult", true);
            ListTimeoutKeepsState();
            PreviewTimeoutAndLateOwnership();
            RetainedThumbnailsSettle();
            SupersededAndDisposedRequests();
            ResponsiveFooter();
            Console.WriteLine("PASS: " + assertions + " recent-snips timeout checks; synthetic providers, unshown controls, no real Shell history or clipboard.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
