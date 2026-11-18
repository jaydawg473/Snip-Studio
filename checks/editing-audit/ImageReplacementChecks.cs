using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SnipStudio;

// Compile against the reviewed application, then sign and verify this executable
// before running it. Fixtures are generated; installed settings are not used.
internal static class ImageReplacementChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); checks++; }

    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Instance).GetValue(owner); }

    private static void SetField(object owner, string name, object value)
    { owner.GetType().GetField(name, Instance).SetValue(owner, value); }

    private static Bitmap Fixture(Color background, Size size)
    {
        Bitmap bitmap = new Bitmap(size.Width, size.Height);
        using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(background);
        return bitmap;
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static void BeginDraft(EditorCanvas canvas, bool recognized)
    {
        canvas.Tool = EditorTool.Text;
        if (!recognized)
        {
            MethodInfo begin = typeof(EditorCanvas).GetMethod("BeginInlineTextNote", Instance, null, new Type[] { typeof(PointF) }, null);
            Check((bool)begin.Invoke(canvas, new object[] { new PointF(35, 65) }), "ordinary draft opens");
            return;
        }
        TextLineRegion line = new TextLineRegion { Text = "SOURCE-00123", Bounds = new RectangleF(18, 60, 145, 15) };
        line.Words.Add(new TextWordRegion { Text = line.Text, Bounds = line.Bounds });
        TextRecognitionResult result = new TextRecognitionResult { Available = true, Text = line.Text };
        result.Lines.Add(line);
        TextOverlaySelection selection = new TextOverlaySelection(result.Lines);
        SetField(canvas, "recognizedText", result);
        SetField(canvas, "recognizedBase", canvas.ImageIdentity);
        SetField(canvas, "recognizedRevision", canvas.DocumentRevision);
        SetField(canvas, "textSelection", selection);
        typeof(EditorCanvas).GetMethod("StartRecognizedTextSession", Instance).Invoke(canvas, new object[] { result, selection });
        Type rich = typeof(EditorCanvas).Assembly.GetType("SnipStudio.RichTextLayout", true);
        string rtf = (string)rich.GetMethod("CreatePlainRtf", Static).Invoke(null,
            new object[] { line.Text, "Segoe UI", 13f, FontStyle.Regular, Color.Black });
        MethodInfo sourceBegin = typeof(EditorCanvas).GetMethod("BeginRecognizedInlineDraft", Instance);
        Check((bool)sourceBegin.Invoke(canvas, new object[] { new Rectangle(16, 58, 149, 19), line.Text, rtf,
            Color.White, 185f, 28f, 0, 0, null }), "recognized proxy draft opens");
        Check(canvas.RecognizedTextSessionActive, "recognized source session is active");
    }

    private static void RejectReplacement(EditorCanvas canvas, string label, Action reject)
    {
        EditorDocument document = (EditorDocument)Field(canvas, "document");
        RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
        string text = draft.Text, rtf = draft.Rtf;
        int selectionStart = draft.SelectionStart, selectionLength = draft.SelectionLength;
        object identity = canvas.ImageIdentity;
        long revision = canvas.DocumentRevision;
        bool canUndo = canvas.CanUndo, canRedo = canvas.CanRedo, dirty = canvas.IsDirty;
        bool recognized = canvas.RecognizedTextSessionActive;
        object session = Field(canvas, "recognizedSessionResult");
        int annotations = document.AnnotationCount;
        using (Bitmap before = document.RenderImage())
        {
            bool rejected = false;
            try { reject(); }
            catch (Exception) { rejected = true; }
            Check(rejected, label + ": invalid replacement fails");
            Check(canvas.InlineTextEditing && Object.ReferenceEquals(draft, Field(canvas, "inlineTextEditor")) && !draft.IsDisposed,
                label + ": exact active draft is retained");
            Check(draft.Text == text && draft.Rtf == rtf, label + ": draft text and rich formatting are retained");
            Check(draft.SelectionStart == selectionStart && draft.SelectionLength == selectionLength,
                label + ": draft selection is retained");
            Check(Object.ReferenceEquals(identity, canvas.ImageIdentity) && revision == canvas.DocumentRevision,
                label + ": source identity and revision are retained");
            Check(canvas.CanUndo == canUndo && canvas.CanRedo == canRedo && canvas.IsDirty == dirty,
                label + ": history availability and dirty state are retained");
            Check(document.AnnotationCount == annotations, label + ": annotations are retained");
            Check(canvas.RecognizedTextSessionActive == recognized && Object.ReferenceEquals(session, Field(canvas, "recognizedSessionResult")),
                label + ": recognition session is retained");
            using (Bitmap after = document.RenderImage()) Check(SamePixels(before, after), label + ": every committed source pixel is retained");
        }
    }

    private static void Run(bool recognized)
    {
        string mode = recognized ? "recognized" : "ordinary";
        using (EditorCanvas canvas = new EditorCanvas())
        using (Bitmap original = Fixture(Color.White, new Size(240, 160)))
        {
            canvas.Size = new Size(700, 500);
            canvas.CreateControl();
            canvas.SetImage(original);
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            document.MarkSaved();
            document.AddRectangle(new RectangleF(3, 3, 24, 18), Color.Red, 2);
            document.AddStroke(new PointF[] { new PointF(45, 12), new PointF(80, 12) }, Color.Blue, 2, false);
            document.Undo();
            Check(document.CanUndo && document.CanRedo, mode + ": fixture has Undo and Redo history");
            BeginDraft(canvas, recognized);
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Text = "DRAFT-00123\nKeep exact D0O and quantities 0.125";
            draft.Select(2, 7);
            int changedEvents = 0;
            canvas.DocumentChanged += delegate { changedEvents++; };
            RejectReplacement(canvas, mode + " null", delegate { canvas.SetImage(null); });
            Bitmap disposed = Fixture(Color.Orange, new Size(12, 12));
            disposed.Dispose();
            RejectReplacement(canvas, mode + " disposed", delegate { canvas.SetImage(disposed); });
            Check(changedEvents == 0, mode + ": rejected replacements publish no document change");
            object previousIdentity = canvas.ImageIdentity;
            using (Bitmap replacement = Fixture(Color.Gold, new Size(150, 90)))
            {
                canvas.SetImage(replacement);
                Check(!canvas.InlineTextEditing && draft.IsDisposed, mode + ": successful replacement closes old draft");
                Check(!canvas.RecognizedTextSessionActive, mode + ": successful replacement closes old recognition session");
                Check(!Object.ReferenceEquals(previousIdentity, canvas.ImageIdentity) && canvas.ImageSize == replacement.Size,
                    mode + ": successful replacement installs the new image");
                Check(!canvas.CanUndo && !canvas.CanRedo && document.AnnotationCount == 0,
                    mode + ": successful replacement starts clean image history");
                using (Bitmap actual = document.RenderImage()) Check(SamePixels(replacement, actual), mode + ": successful replacement copies exact new pixels");
                Check(changedEvents == 1, mode + ": successful replacement publishes one document change");
            }
        }
    }

    private static void PendingDraftHistory()
    {
        using (EditorCanvas canvas = new EditorCanvas())
        using (Bitmap image = Fixture(Color.White, new Size(240, 160)))
        {
            canvas.Size = new Size(700, 500);
            canvas.CreateControl();
            canvas.SetImage(image);
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            document.MarkSaved();
            BeginDraft(canvas, false);
            ((RichTextBox)Field(canvas, "inlineTextEditor")).Text = "FIRST-00123";
            Check(!document.CanUndo && canvas.CanUndo, "pending new note offers Undo before document history exists");
            canvas.Undo();
            Check(!canvas.InlineTextEditing && document.AnnotationCount == 0 && canvas.CanRedo,
                "Undo removes the pending new note while retaining its Redo transaction");
            canvas.Redo();
            Check(document.AnnotationCount == 1 && document.GetTextAnnotation(0).Text == "FIRST-00123",
                "Redo restores the exact text of the undone pending note");
        }

        using (EditorCanvas canvas = new EditorCanvas())
        using (Bitmap image = Fixture(Color.White, new Size(240, 160)))
        {
            canvas.Size = new Size(700, 500);
            canvas.CreateControl();
            canvas.SetImage(image);
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            document.MarkSaved();
            document.AddText("COMMITTED-00123", new PointF(8, 8), Color.Black, 13, 170);
            canvas.Undo();
            Check(document.CanRedo && canvas.CanRedo, "committed-note Undo exposes the existing Redo branch");
            BeginDraft(canvas, false);
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Text = "UNAPPLIED-00999";
            draft.Select(1, 7);
            string rtf = draft.Rtf;
            long revision = canvas.DocumentRevision;
            object identity = canvas.ImageIdentity;
            int documentEvents = 0;
            canvas.DocumentChanged += delegate { documentEvents++; };
            Check(document.CanRedo && !canvas.CanRedo, "pending draft withholds document Redo without destroying its branch");
            canvas.Redo();
            Check(canvas.InlineTextEditing && Object.ReferenceEquals(draft, Field(canvas, "inlineTextEditor")) && !draft.IsDisposed,
                "direct Redo retains the active draft editor");
            Check(draft.Text == "UNAPPLIED-00999" && draft.Rtf == rtf && draft.SelectionStart == 1 && draft.SelectionLength == 7,
                "direct Redo preserves exact unapplied draft, formatting and selection");
            Check(canvas.DocumentRevision == revision && Object.ReferenceEquals(identity, canvas.ImageIdentity) &&
                document.AnnotationCount == 0 && document.CanRedo && !document.IsDirty && documentEvents == 0,
                "direct Redo applies nothing and preserves the saved document and existing history");
            canvas.CancelInlineText();
            Check(canvas.CanRedo, "canceling the new draft restores document Redo availability");
            canvas.Redo();
            Check(document.AnnotationCount == 1 && document.GetTextAnnotation(0).Text == "COMMITTED-00123",
                "Redo after cancel restores the original committed annotation");
            Check(documentEvents == 1, "only the actual Redo publishes a document change");
        }
    }

    private static void LowZoomDrafts(bool recognized)
    {
        string mode = recognized ? "recognized" : "ordinary";
        using (EditorCanvas canvas = new EditorCanvas())
        using (Bitmap image = Fixture(Color.White, new Size(240, 160)))
        {
            canvas.Size = new Size(700, 500);
            canvas.CreateControl();
            canvas.SetImage(image);
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            document.MarkSaved();
            canvas.ZoomAt(.01f, new Point(350, 250));
            Check(Math.Abs(canvas.Zoom - .01f) < .00001f, mode + ": legitimate one-percent canvas view is retained before editing");
            BeginDraft(canvas, recognized);
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            if (!recognized) draft.Text = "ZOOM-00123";
            draft.Select(2, 4);
            string text = draft.Text, rtf = draft.Rtf;
            long revision = canvas.DocumentRevision;
            object identity = canvas.ImageIdentity;
            using (Bitmap before = document.RenderImage())
            {
                foreach (float requested in new float[] { .01f, 1f / 64f, .02f, .5f, 8f })
                {
                    canvas.ZoomAt(requested, new Point(350, 250));
                    Check(Math.Abs(canvas.Zoom - requested) < .00001f,
                        mode + ": canvas keeps requested source zoom " + requested);
                    Check(canvas.InlineTextEditing && Object.ReferenceEquals(draft, Field(canvas, "inlineTextEditor")),
                        mode + ": changing zoom retains the same draft");
                    Check(draft.ZoomFactor > 1f / 64f && draft.ZoomFactor < 64f,
                        mode + ": native editor zoom stays strictly inside accepted bounds");
                    if (requested <= .02f)
                        Check(draft.ZoomFactor >= .019f && draft.ZoomFactor <= .025f,
                            mode + ": low source zoom uses a supported small editor surface rather than native default zoom");
                    Check(draft.Text == text && draft.Rtf == rtf && draft.SelectionStart == 2 && draft.SelectionLength == 4,
                        mode + ": zoom changes no text, formatting or selected characters");
                    Check(canvas.DocumentRevision == revision && Object.ReferenceEquals(identity, canvas.ImageIdentity),
                        mode + ": zoom creates no document transaction");
                }
                using (Bitmap after = document.RenderImage())
                    Check(SamePixels(before, after), mode + ": every source pixel is retained across zoom limits");
            }
            canvas.CancelInlineText();
        }
    }

    private static void PendingDraftHistoryButtons()
    {
        using (MainForm form = (MainForm)Activator.CreateInstance(typeof(MainForm), Instance, null, new object[] { null, true }, null))
        using (Bitmap image = Fixture(Color.White, new Size(240, 160)))
        {
            EditorCanvas canvas = (EditorCanvas)Field(form, "canvas");
            canvas.SetImage(image);
            Button undo = (Button)Field(form, "undoButton");
            Button redo = (Button)Field(form, "redoButton");
            Check(!undo.Enabled && !redo.Enabled, "fresh source has neither history button available");
            BeginDraft(canvas, false);
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Text = "BUTTONS-00123";
            Check(undo.Enabled && !redo.Enabled, "typing a fresh draft immediately enables toolbar Undo");
            canvas.Undo();
            Check(!undo.Enabled && redo.Enabled, "undoing the draft immediately enables toolbar Redo");
            BeginDraft(canvas, false);
            draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Text = "NEW-BRANCH-00999";
            Check(undo.Enabled && !redo.Enabled, "typing another draft immediately disables the document Redo button");
            canvas.CancelInlineText();
            Check(!undo.Enabled && redo.Enabled, "canceling that draft restores the toolbar's original Redo availability");
            canvas.Redo();
            Check(undo.Enabled && !redo.Enabled, "completing the actual Redo refreshes both history buttons");
        }
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Run(false);
            Run(true);
            PendingDraftHistory();
            LowZoomDrafts(false);
            LowZoomDrafts(true);
            PendingDraftHistoryButtons();
            checks += PasteFeedbackChecks.Run();
            Console.WriteLine("PASS: " + checks + " image replacement failure/history checks; generated fixtures and unshown controls.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
