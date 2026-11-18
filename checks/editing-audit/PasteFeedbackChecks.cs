using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SnipStudio;

// Production insertion path with detached supplied clipboard values. The real
// clipboard, installed settings and application entrypoint remain untouched.
internal static class PasteFeedbackChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); checks++; }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Flags).GetValue(owner); }
    private static bool Paste(RichTextBox draft, string plain, string rich)
    { return (bool)draft.GetType().GetMethod("PasteTextContent", Flags).Invoke(draft, new object[] { plain, rich }); }
    private static string Rich(string text, Color color, FontStyle style)
    {
        using (RichTextBox box = new RichTextBox())
        using (Font font = new Font("Segoe UI", 12, style))
        { box.Font = font; box.ForeColor = color; box.Text = text; return box.Rtf; }
    }
    private static void Rejected(EditorCanvas canvas, RichTextBox draft, string plain, string rich, string reason)
    {
        string text = draft.Text, rtf = draft.Rtf;
        int start = draft.SelectionStart, length = draft.SelectionLength;
        long revision = canvas.DocumentRevision;
        Check(!Paste(draft, plain, rich), reason + ": rejects paste");
        Check(Object.ReferenceEquals(draft, Field(canvas, "inlineTextEditor")) && draft.Text == text && draft.Rtf == rtf,
            reason + ": retains same complete formatted draft");
        Check(draft.SelectionStart == start && draft.SelectionLength == length, reason + ": retains selection");
        Check(canvas.DocumentRevision == revision, reason + ": creates no document transaction");
        Check(!String.IsNullOrWhiteSpace(canvas.InlineTextStatus) && canvas.InlineTextStatus.IndexOf("draft", StringComparison.OrdinalIgnoreCase) >= 0,
            reason + ": explains failure and retained draft");
    }

    private static void RejectedBudget(RichTextBox draft, Func<string, string, bool> paste, Func<string> feedback, string rich, string reason)
    {
        string text = draft.Text, rtf = draft.Rtf;
        int start = draft.SelectionStart, length = draft.SelectionLength;
        Assert(!paste(null, rich), reason + ": rejects the actual oversized native insertion");
        Assert(draft.Text == text && draft.Rtf == rtf && draft.SelectionStart == start && draft.SelectionLength == length,
            reason + ": retains complete formatted draft and selection");
        Assert(feedback().IndexOf("16,000", StringComparison.Ordinal) >= 0, reason + ": explains the character limit");
    }

    private static void Assert(bool value, string message) { Check(value, message); }

    private static void RichBudgetCases(RichTextBox draft, Func<string, string, bool> paste, Func<string> feedback, string mode)
    {
        string oneParagraph = Rich("RICH-00045", Color.Blue, FontStyle.Bold);
        draft.Text = new string('B', 15990);
        draft.Select(draft.TextLength, 0);
        // Full-document Text reports ten characters, but inserting its explicit
        // terminal paragraph adds eleven. This formerly admitted 16,001 chars.
        RejectedBudget(draft, paste, feedback, oneParagraph, mode + " terminal paragraph at note limit");
        draft.Select(draft.TextLength - 1, 1);
        Assert(paste(null, oneParagraph), mode + ": replacing one selected character admits the complete 16,000-character result");
        Assert(draft.TextLength == 16000 && draft.Text == new string('B', 15989) + "RICH-00045\n",
            mode + ": retains the exact rich identifier and legitimate terminal paragraph");

        string twoParagraphs = Rich("LINE-01\nLINE-02", Color.Blue, FontStyle.Bold);
        draft.Text = new string('B', 15985);
        draft.Select(draft.TextLength, 0);
        RejectedBudget(draft, paste, feedback, twoParagraphs, mode + " multiline rich text at note limit");
        draft.Select(draft.TextLength - 1, 1);
        Assert(paste(null, twoParagraphs), mode + ": selection replacement admits a complete multiline result at the limit");
        Assert(draft.TextLength == 16000 && draft.Text == new string('B', 15984) + "LINE-01\nLINE-02\n",
            mode + ": multiline text and terminal paragraph are preserved without trimming");

        draft.Text = "BEFORE-0001\nREPLACE-0002\nAFTER-0003";
        int at = draft.Text.IndexOf("REPLACE-0002", StringComparison.Ordinal);
        draft.Select(at, "REPLACE-0002".Length);
        string nativeExpected;
        using (RichTextBox reference = new RichTextBox { Multiline = true, MaxLength = Int32.MaxValue, DetectUrls = false })
        {
            reference.Rtf = draft.Rtf;
            reference.Select(draft.SelectionStart, draft.SelectionLength);
            reference.SelectedRtf = twoParagraphs;
            nativeExpected = reference.Text;
        }
        Assert(paste(null, twoParagraphs), mode + ": accepts a valid paragraph insertion within an existing multiline draft");
        Assert(draft.Text == nativeExpected && draft.Text.StartsWith("BEFORE-0001\n", StringComparison.Ordinal) &&
            draft.Text.EndsWith("AFTER-0003", StringComparison.Ordinal), mode + ": target paragraph context follows independent native insertion semantics");
    }

    private static void AdvancedPasteCases()
    {
        Type dialog = typeof(EditorCanvas).Assembly.GetType("SnipStudio.TextEditorDialog", true);
        Type editorType = dialog.GetNestedType("RichTextDraftBox", BindingFlags.NonPublic);
        using (RichTextBox editor = (RichTextBox)Activator.CreateInstance(editorType, true))
        {
            editor.Multiline = true;
            editor.MaxLength = 16000;
            MethodInfo insertion = editorType.GetMethod("PasteTextContent", Flags);
            string message = "";
            Func<string, string, bool> paste = delegate(string plain, string rich)
            {
                object[] arguments = { plain, rich, null };
                bool accepted = (bool)insertion.Invoke(editor, arguments);
                message = (string)arguments[2] ?? "";
                return accepted;
            };
            RichBudgetCases(editor, paste, delegate { return message; }, "advanced editor");
            editor.SelectAll();
            Assert(paste(new string('Q', 16001), Rich("RICH-00045", Color.Blue, FontStyle.Bold)),
                "advanced editor: supported rich representation takes precedence over an oversized differing plain representation");
            Assert(editor.Text == "RICH-00045\n", "advanced editor: preserves the complete selected RTF paragraph");
        }
    }
    internal static int Run()
    {
        using (EditorCanvas canvas = new EditorCanvas())
        using (Bitmap image = new Bitmap(400, 200))
        {
            using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
            canvas.SetImage(image);
            MethodInfo begin = typeof(EditorCanvas).GetMethod("BeginInlineTextNote", Flags, null, new Type[] { typeof(PointF) }, null);
            Check((bool)begin.Invoke(canvas, new object[] { new PointF(20, 30) }), "paste: starts inline draft");
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Text = "BASE-00123";
            draft.Select(2, 3);
            Rejected(canvas, draft, new string('P', 16001), null, "oversized plain text");
            Check(canvas.InlineTextStatus.IndexOf("16,000", StringComparison.Ordinal) >= 0, "paste: states character limit");
            Rejected(canvas, draft, null, null, "clipboard without text");
            Rejected(canvas, draft, null, "not valid RTF", "unsupported rich text");

            string before = draft.Text;
            int at = draft.SelectionStart, selected = draft.SelectionLength;
            long revision = canvas.DocumentRevision;
            Check(Paste(draft, "FALLBACK-00017", "not valid RTF"), "paste: unsupported rich content uses supplied plain fallback");
            Check(draft.Text == before.Substring(0, at) + "FALLBACK-00017" + before.Substring(at + selected),
                "paste: plain fallback preserves exact identifier and surrounding text");
            Check(canvas.DocumentRevision == revision, "paste: fallback remains an unapplied draft");
            Check(canvas.InlineTextStatus.IndexOf("Text pasted", StringComparison.Ordinal) >= 0, "paste: success replaces stale error feedback");

            draft.SelectAll();
            string rich = Rich("RICH-00045", Color.Blue, FontStyle.Bold);
            Check(Paste(draft, new string('Q', 16001), rich), "paste: supported rich content takes precedence over a differing plain representation");
            Check(draft.Text == "RICH-00045\n", "paste: retains exact rich text and its explicit terminal paragraph; actual=" +
                draft.Text.Replace("\r", "\\r").Replace("\n", "\\n") + "; length=" + draft.TextLength);
            draft.Select(0, "RICH-00045".Length);
            using (Font font = draft.SelectionFont)
                Check(font != null && font.Bold && draft.SelectionColor.ToArgb() == Color.Blue.ToArgb(), "paste: retains rich formatting");

            draft.SelectAll();
            Rejected(canvas, draft, null, Rich(new string('R', 16001), Color.Black, FontStyle.Regular), "oversized rich text");
            draft.SelectAll();
            string exactLimit = new string('L', 16000);
            Check(Paste(draft, exactLimit, null), "paste: accepts exact character limit when replacing selection");
            Check(draft.Text == exactLimit, "paste: exact limit is neither truncated nor rewritten");
            Check(canvas.DocumentRevision == revision, "paste: accepted content remains unapplied until finish");
            RichBudgetCases(draft, delegate(string plain, string formatted) { return Paste(draft, plain, formatted); },
                delegate { return canvas.InlineTextStatus; }, "inline editor");
            Check(canvas.DocumentRevision == revision, "paste: rich budget checks create no document transaction");
            canvas.CancelInlineText();
        }
        AdvancedPasteCases();
        Console.WriteLine("PASS: " + checks + " paste feedback checks; supplied clipboard values only");
        return checks;
    }
}
