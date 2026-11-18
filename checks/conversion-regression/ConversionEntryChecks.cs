using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SnipStudio;

internal static class ConversionEntryChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        checks++;
    }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Instance).GetValue(owner); }
    private static void Field(object owner, string name, object value)
    { owner.GetType().GetField(name, Instance).SetValue(owner, value); }
    private static object Invoke(object owner, string name, params object[] args)
    { return owner.GetType().GetMethod(name, Instance).Invoke(owner, args); }
    private static MainForm Form()
    {
        return (MainForm)Activator.CreateInstance(typeof(MainForm), Instance, null, new object[] { null, true }, null);
    }
    private static Bitmap Fixture()
    {
        Bitmap image = new Bitmap(400, 160);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Red, 12, 22, 12, 16);
            graphics.FillRectangle(Brushes.Black, 35, 28, 100, 7);
        }
        return image;
    }
    private static void Seed(EditorCanvas canvas, Bitmap image)
    {
        EditorDocument document = (EditorDocument)Field(canvas, "document");
        document.SetImage(image);
        TextLineRegion line = new TextLineRegion { Text = "Sample-file.pdf", Bounds = new RectangleF(35, 25, 100, 11) };
        line.Words.Add(new TextWordRegion { Text = line.Text, Bounds = line.Bounds });
        TextRecognitionResult result = new TextRecognitionResult { Available = true, Text = line.Text };
        result.Lines.Add(line);
        Field(canvas, "recognizedText", result);
        Field(canvas, "recognizedBase", canvas.ImageIdentity);
        Field(canvas, "recognizedRevision", canvas.DocumentRevision);
        Field(canvas, "textSelection", new TextOverlaySelection(result.Lines));
    }
    private static bool SamePixels(Bitmap left, Bitmap right)
    {
        if (left.Size != right.Size) return false;
        for (int y = 0; y < left.Height; y++)
            for (int x = 0; x < left.Width; x++)
                if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb()) return false;
        return true;
    }
    private static void SelectedTextButton(MainForm form, string context)
    {
        Dictionary<EditorTool, Button> buttons = (Dictionary<EditorTool, Button>)Field(form, "toolButtons");
        foreach (KeyValuePair<EditorTool, Button> entry in buttons)
        {
            bool selected = (bool)entry.Value.GetType().GetProperty("Selected", Instance).GetValue(entry.Value, null);
            Check(selected == (entry.Key == EditorTool.Text), context + ": only Text toolbar button selected (" + entry.Key + ")");
        }
    }
    private static void Entry(EditorTool previous)
    {
        using (MainForm form = Form())
        using (Bitmap image = Fixture())
        {
            EditorCanvas canvas = (EditorCanvas)Field(form, "canvas");
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            Seed(canvas, image);
            Invoke(form, "SelectTool", previous);
            long revision = canvas.DocumentRevision;
            object identity = canvas.ImageIdentity;
            bool wasDirty = canvas.IsDirty, hadUndo = canvas.CanUndo;
            int annotations = document.AnnotationCount;
            using (Bitmap before = canvas.RenderImage())
            {
                Invoke(form, "CopyText");
                Check(canvas.Tool == EditorTool.Text, previous + ": conversion activates Text tool");
                Check(canvas.TextActionsMode, previous + ": conversion opens recognized-text actions");
                SelectedTextButton(form, previous + " entry");
                Check((bool)Field(form, "textActionsButton").GetType().GetProperty("Selected", Instance).GetValue(Field(form, "textActionsButton"), null), previous + ": recognition button selected");
                Check(Object.ReferenceEquals(identity, canvas.ImageIdentity) && revision == canvas.DocumentRevision, previous + ": entry does not replace the image or create a transaction");
                Check(document.AnnotationCount == annotations && canvas.CanUndo == hadUndo && canvas.IsDirty == wasDirty, previous + ": entry preserves annotation, dirty and Undo state");
                using (Bitmap after = canvas.RenderImage()) Check(SamePixels(before, after), previous + ": entry preserves every source pixel");
                Invoke(form, "CopyText");
                Check(!canvas.TextActionsMode && canvas.Tool == EditorTool.Text, previous + ": toggle closes actions and leaves Text active");
                SelectedTextButton(form, previous + " close");
                using (Bitmap after = canvas.RenderImage()) Check(SamePixels(before, after), previous + ": closing actions preserves every source pixel");
            }
        }
    }
    private static void StaleDraft()
    {
        using (MainForm form = Form())
        using (Bitmap image = Fixture())
        {
            EditorCanvas canvas = (EditorCanvas)Field(form, "canvas");
            Seed(canvas, image);
            Invoke(form, "SelectTool", EditorTool.Text);
            canvas.ActualSize();
            MethodInfo begin = typeof(EditorCanvas).GetMethod("BeginInlineTextNote", Instance, null, new Type[] { typeof(PointF) }, null);
            Check((bool)begin.Invoke(canvas, new object[] { new PointF(32, 70) }), "stale draft: starts a draft");
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Text = "KEEP DRAFT";
            ((EditorDocument)Field(canvas, "document")).SetImage(image);
            Invoke(form, "CopyText");
            Check(canvas.InlineTextEditing && Object.ReferenceEquals(draft, Field(canvas, "inlineTextEditor")), "stale draft: conversion keeps the same editor");
            Check(draft.Text == "KEEP DRAFT", "stale draft: conversion keeps entered text");
            Check(!canvas.TextActionsMode, "stale draft: conversion does not open actions when commit fails");
            Check(canvas.InlineTextStatus.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0, "stale draft: reports changed source");
            canvas.CancelInlineText();
        }
    }
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Entry(EditorTool.Pen);
            Entry(EditorTool.Highlighter);
            Entry(EditorTool.Select);
            StaleDraft();
            Console.WriteLine("PASS: " + checks + " conversion entry checks; production MainForm, unshown controls, seeded OCR; no app entrypoint or installed settings.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
