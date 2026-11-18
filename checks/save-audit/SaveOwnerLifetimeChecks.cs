using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio Save Owner Lifetime Checks")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Sign before execution. Briefly shows only a synthetic save-progress window.
internal static class SaveOwnerLifetimeChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); checks++; }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected exact app hash and output JSON path.");
            string app = typeof(MainForm).Assembly.Location;
            using (SHA256 sha = SHA256.Create())
            using (Stream input = File.OpenRead(app))
                Check(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").Equals(args[0], StringComparison.OrdinalIgnoreCase), "Exact selected app");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = (MainForm)Activator.CreateInstance(typeof(MainForm), Flags, null, new object[] { null, true }, null))
            using (Bitmap image = new Bitmap(80, 60))
            {
                using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.Crimson);
                EditorCanvas canvas = (EditorCanvas)typeof(MainForm).GetField("canvas", Flags).GetValue(form);
                canvas.SetImage(image);
                object identity = canvas.ImageIdentity; long revision = canvas.DocumentRevision;
                using (Bitmap frozen = canvas.RenderImage())
                using (System.Windows.Forms.Timer dispose = new System.Windows.Forms.Timer { Interval = 25 })
                {
                    bool disposedDuringWork = false, workerUsedBitmapAfterOwnerDisposal = false, workerCompleted = false;
                    dispose.Tick += delegate { dispose.Stop(); form.Dispose(); disposedDuringWork = true; };
                    dispose.Start();
                    Call(form, "RunImageSaveWithProgress", new Action(delegate
                    {
                        Thread.Sleep(180);
                        workerUsedBitmapAfterOwnerDisposal = frozen.GetPixel(30, 30).ToArgb() == Color.Crimson.ToArgb();
                        workerCompleted = true;
                    }));
                    dispose.Stop();
                    Check(disposedDuringWork && form.IsDisposed, "Synthetic owner was forcibly disposed during work");
                    Check(workerCompleted && workerUsedBitmapAfterOwnerDisposal, "Worker completes with its frozen bitmap alive after owner disposal");
                    Check(!(bool)typeof(MainForm).GetProperty("IsSavingImage", Flags).GetValue(form, null), "Teardown clears pending save state");
                    Check(!(bool)Call(form, "CompleteImageSave", "unused.png", identity, revision), "Disposed owner cannot receive path or saved-state updates");
                }
            }
            string report = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { Passed = true, Assertions = checks,
                AppSHA256 = args[0].ToUpperInvariant(), CheckedUtc = DateTimeOffset.UtcNow.ToString("o"),
                Scope = "Synthetic native modal-owner disposal and frozen-bitmap lifetime. No app entrypoint, user files, preferences, clipboard or physical input." }));
            Console.WriteLine("PASS: " + checks + " save owner-lifetime checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
