using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // Each command sets only its own native formatting mask. Reconstructing
    // SelectionFont would discard unrelated attributes in a mixed selection.
    internal static class RichEditFormatting
    {
        private const int EmGetCharFormat = 0x0400 + 58;
        private const int EmSetCharFormat = 0x0400 + 68;
        private const int EmGetParaFormat = 0x0400 + 61;
        private const int EmSetParaFormat = 0x0400 + 71;
        private const int ScfSelection = 1;

        private const uint CfmBold = 0x00000001;
        private const uint CfmItalic = 0x00000002;
        private const uint CfmUnderline = 0x00000004;
        private const uint CfmStrikeout = 0x00000008;
        private const uint CfmSize = 0x80000000;
        private const uint CfmColor = 0x40000000;
        private const uint CfmFace = 0x20000000;
        private const uint CfmOffset = 0x10000000;
        private const uint CfmBackColor = 0x04000000;
        private const uint CfmScript = 0x00030000;
        private const uint CfeSubscript = 0x00010000;
        private const uint CfeSuperscript = 0x00020000;
        private const uint CfeAutoBackColor = 0x04000000;
        private const uint CfmSpacing = 0x00200000;
        private const uint CfmWeight = 0x00400000;

        private const uint PfmStartIndent = 0x00000001;
        private const uint PfmRightIndent = 0x00000002;
        private const uint PfmOffset = 0x00000004;
        private const uint PfmAlignment = 0x00000008;
        private const uint PfmNumbering = 0x00000020;
        private const uint PfmSpaceBefore = 0x00000040;
        private const uint PfmSpaceAfter = 0x00000080;
        private const uint PfmLineSpacing = 0x00000100;
        private const uint PfmNumberingStyle = 0x00002000;
        private const uint PfmNumberingTab = 0x00004000;
        private const uint PfmNumberingStart = 0x00008000;

        // Win32 Richedit.h uses four-byte packing for these pointer-free
        // structures. CHARFORMAT2W is 116 bytes; PARAFORMAT2 is 188 bytes.
        [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode, Pack = 4)]
        private struct CharacterFormat
        {
            internal uint Size;
            internal uint Mask;
            internal uint Effects;
            internal int Height;
            internal int Offset;
            internal int TextColor;
            internal byte CharSet;
            internal byte PitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FaceName;
            internal ushort Weight;
            internal short Spacing;
            internal int BackColor;
            internal uint Locale;
            internal uint Reserved;
            internal short Style;
            internal ushort Kerning;
            internal byte UnderlineType;
            internal byte Animation;
            internal byte RevisionAuthor;
            internal byte UnderlineColor;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ParagraphFormat
        {
            internal uint Size;
            internal uint Mask;
            internal ushort Numbering;
            internal ushort Effects;
            internal int StartIndent;
            internal int RightIndent;
            internal int Offset;
            internal ushort Alignment;
            internal short TabCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal int[] Tabs;
            internal int SpaceBefore;
            internal int SpaceAfter;
            internal int LineSpacing;
            internal short Style;
            internal byte LineSpacingRule;
            internal byte OutlineLevel;
            internal ushort ShadingWeight;
            internal ushort ShadingStyle;
            internal ushort NumberingStart;
            internal ushort NumberingStyle;
            internal ushort NumberingTab;
            internal ushort BorderSpace;
            internal ushort BorderWidth;
            internal ushort Borders;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendCharacterFormat(IntPtr window, int message, IntPtr parameter, ref CharacterFormat format);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendParagraphFormat(IntPtr window, int message, IntPtr parameter, ref ParagraphFormat format);

        internal static void SetFontFamily(RichTextBox box, string family)
        {
            family = ValidateFamily(family);
            CharacterFormat format = NewCharacterFormat(CfmFace);
            format.FaceName = family;
            SetCharacters(box, ref format);
        }

        internal static void SetFontSizePixels(RichTextBox box, float pixels)
        {
            ValidateFiniteRange(pixels, 1, 1024, "pixels");
            CharacterFormat format = NewCharacterFormat(CfmSize);
            format.Height = (int)Math.Round(pixels * 15.0, MidpointRounding.AwayFromZero);
            SetCharacters(box, ref format);
        }

        internal static void SetStyle(RichTextBox box, FontStyle style, bool enabled)
        {
            uint mask = StyleMask(style);
            if (mask == 0) return;
            CharacterFormat format = NewCharacterFormat(mask);
            format.Effects = enabled ? mask : 0;
            SetCharacters(box, ref format);
        }

        internal static void SetTextColor(RichTextBox box, Color color)
        {
            CharacterFormat format = NewCharacterFormat(CfmColor);
            format.TextColor = ColorRef(color);
            SetCharacters(box, ref format);
        }

        internal static void SetHighlightColor(RichTextBox box, Color? color)
        {
            CharacterFormat format = NewCharacterFormat(CfmBackColor);
            if (color.HasValue) format.BackColor = ColorRef(color.Value);
            else format.Effects = CfeAutoBackColor;
            SetCharacters(box, ref format);
        }

        // Native sub/superscript effects preserve each run's stored font size.
        internal static void SetScript(RichTextBox box, int direction)
        {
            if (direction < -1 || direction > 1) throw new ArgumentOutOfRangeException("direction");
            CharacterFormat format = NewCharacterFormat(CfmScript | CfmOffset);
            format.Effects = direction < 0 ? CfeSubscript : direction > 0 ? CfeSuperscript : 0;
            format.Offset = 0;
            SetCharacters(box, ref format);
        }

        // Public UI order is left, center, right, justify; Win32 swaps right/center.
        internal static void ParagraphAlign(RichTextBox box, int alignment)
        {
            if (alignment < 0 || alignment > 3) throw new ArgumentOutOfRangeException("alignment");
            ParagraphFormat format = NewParagraphFormat(PfmAlignment);
            format.Alignment = (ushort)(alignment == 1 ? 3 : alignment == 2 ? 2 : alignment == 3 ? 4 : 1);
            SetParagraphs(box, ref format);
        }

        internal static void SetBullets(RichTextBox box, bool enabled) { SetListKind(box, enabled ? 1 : 0); }
        internal static void SetNumbering(RichTextBox box, bool enabled) { SetListKind(box, enabled ? 2 : 0); }

        private static void SetListKind(RichTextBox box, int kind)
        {
            ParagraphFormat format = NewParagraphFormat(PfmNumbering);
            format.Numbering = (ushort)kind;
            if (kind != 0)
            {
                format.Mask |= PfmNumberingStyle | PfmNumberingStart | PfmNumberingTab;
                format.NumberingStyle = kind == 2 ? (ushort)0x0200 : (ushort)0;
                format.NumberingStart = 1;
                format.NumberingTab = 360;
            }
            SetParagraphs(box, ref format);
        }

        internal static void SetIndent(RichTextBox box, int pixels)
        {
            if (pixels < 0 || pixels > 4096) throw new ArgumentOutOfRangeException("pixels");
            ParagraphFormat format = NewParagraphFormat(PfmStartIndent);
            format.StartIndent = checked(pixels * 15);
            SetParagraphs(box, ref format);
        }

        internal static void ChangeIndent(RichTextBox box, int deltaPixels)
        {
            RequireControl(box);
            if (deltaPixels < -4096 || deltaPixels > 4096) throw new ArgumentOutOfRangeException("deltaPixels");
            if (deltaPixels == 0) return;
            int selectionStart = box.SelectionStart, selectionLength = box.SelectionLength;
            string text = box.Text;
            int end = selectionLength == 0 ? selectionStart : Math.Min(text.Length, selectionStart + selectionLength) - 1;
            int paragraph = selectionStart <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(text.Length, selectionStart) - 1) + 1;
            List<int> paragraphs = new List<int>();
            paragraphs.Add(paragraph);
            int next;
            while ((next = text.IndexOf('\n', paragraph)) >= 0 && next + 1 <= end)
            { paragraph = next + 1; paragraphs.Add(paragraph); }
            try
            {
                // Work paragraph-by-paragraph so a mixed selection retains its
                // relative indents and no paragraph is pushed past the left edge.
                foreach (int position in paragraphs)
                {
                    box.Select(position, 0);
                    ParagraphFormat current = GetParagraphs(box);
                    ParagraphFormat format = NewParagraphFormat(PfmStartIndent);
                    format.StartIndent = (int)Math.Max(0L, Math.Min(4096L * 15, (long)current.StartIndent + deltaPixels * 15L));
                    SetParagraphs(box, ref format);
                }
            }
            finally { box.Select(selectionStart, selectionLength); }
        }

        internal static void SetLineSpacing(RichTextBox box, float multiple)
        {
            ValidateFiniteRange(multiple, 0.5f, 5, "multiple");
            ParagraphFormat format = NewParagraphFormat(PfmLineSpacing);
            format.LineSpacingRule = 5;
            format.LineSpacing = (int)Math.Round(multiple * 20.0, MidpointRounding.AwayFromZero);
            SetParagraphs(box, ref format);
        }

        internal static void SetParagraphSpacing(RichTextBox box, float beforePoints, float afterPoints)
        {
            ValidateFiniteRange(beforePoints, 0, 200, "beforePoints");
            ValidateFiniteRange(afterPoints, 0, 200, "afterPoints");
            ParagraphFormat format = NewParagraphFormat(PfmSpaceBefore | PfmSpaceAfter);
            format.SpaceBefore = (int)Math.Round(beforePoints * 20.0, MidpointRounding.AwayFromZero);
            format.SpaceAfter = (int)Math.Round(afterPoints * 20.0, MidpointRounding.AwayFromZero);
            SetParagraphs(box, ref format);
        }

        internal static void SetSpaceBefore(RichTextBox box, float points)
        {
            ValidateFiniteRange(points, 0, 200, "points");
            ParagraphFormat format = NewParagraphFormat(PfmSpaceBefore);
            format.SpaceBefore = (int)Math.Round(points * 20.0, MidpointRounding.AwayFromZero);
            SetParagraphs(box, ref format);
        }

        internal static void SetSpaceAfter(RichTextBox box, float points)
        {
            ValidateFiniteRange(points, 0, 200, "points");
            ParagraphFormat format = NewParagraphFormat(PfmSpaceAfter);
            format.SpaceAfter = (int)Math.Round(points * 20.0, MidpointRounding.AwayFromZero);
            SetParagraphs(box, ref format);
        }

        internal static float? GetSpaceBefore(RichTextBox box)
        {
            ParagraphFormat format = GetParagraphs(box);
            return (format.Mask & PfmSpaceBefore) != 0 ? (float?)(format.SpaceBefore / 20f) : null;
        }

        internal static float? GetSpaceAfter(RichTextBox box)
        {
            ParagraphFormat format = GetParagraphs(box);
            return (format.Mask & PfmSpaceAfter) != 0 ? (float?)(format.SpaceAfter / 20f) : null;
        }

        internal static float? GetLineSpacing(RichTextBox box)
        {
            ParagraphFormat format = GetParagraphs(box);
            if ((format.Mask & PfmLineSpacing) == 0) return null;
            switch (format.LineSpacingRule)
            {
                case 0: return 1f;
                case 1: return 1.5f;
                case 2: return 2f;
                case 5: return format.LineSpacing / 20f;
                // Exact/minimum distances are in twips, not multiples. Do not
                // reinterpret or overwrite such source formatting on refresh.
                default: return null;
            }
        }

        internal static bool? GetStyleState(RichTextBox box, FontStyle style)
        {
            uint mask = StyleMask(style);
            if (mask == 0) return false;
            CharacterFormat format = GetCharacters(box);
            return (format.Mask & mask) == mask ? (bool?)((format.Effects & mask) == mask) : null;
        }

        internal static int? GetScript(RichTextBox box)
        {
            CharacterFormat format = GetCharacters(box);
            if ((format.Mask & CfmScript) != CfmScript) return null;
            return (format.Effects & CfeSuperscript) != 0 ? 1 : (format.Effects & CfeSubscript) != 0 ? -1 : 0;
        }

        internal static int? GetAlignment(RichTextBox box)
        {
            ParagraphFormat format = GetParagraphs(box);
            if ((format.Mask & PfmAlignment) == 0) return null;
            return format.Alignment == 3 ? 1 : format.Alignment == 2 ? 2 : format.Alignment == 4 ? 3 : 0;
        }

        internal static int? GetListKind(RichTextBox box)
        {
            ParagraphFormat format = GetParagraphs(box);
            if ((format.Mask & PfmNumbering) == 0) return null;
            return format.Numbering == 1 ? 1 : format.Numbering == 2 ? 2 : 0;
        }

        internal static void ClearFormatting(RichTextBox box)
        {
            RequireControl(box);
            ClearFormatting(box, box.Font.FontFamily.Name, box.Font.SizeInPoints * (96f / 72f), box.ForeColor);
        }

        internal static void ClearFormatting(RichTextBox box, string family, float sizePixels, Color color)
        {
            family = ValidateFamily(family);
            ValidateFiniteRange(sizePixels, 1, 1024, "sizePixels");
            // The explicit clear command resets selected character effects,
            // rather than changing unrelated runs outside the selected span.
            const uint effects = 0x00007FFF;
            CharacterFormat characters = NewCharacterFormat(CfmFace | CfmSize | CfmColor | CfmBackColor |
                CfmOffset | CfmScript | CfmSpacing | CfmWeight | effects);
            characters.FaceName = family;
            characters.Height = (int)Math.Round(sizePixels * 15.0, MidpointRounding.AwayFromZero);
            characters.TextColor = ColorRef(color);
            characters.Weight = 400;
            characters.Effects = CfeAutoBackColor;
            SetCharacters(box, ref characters);
            ParagraphFormat paragraphs = NewParagraphFormat(PfmStartIndent | PfmRightIndent | PfmOffset | PfmAlignment |
                PfmNumbering | PfmSpaceBefore | PfmSpaceAfter | PfmLineSpacing | PfmNumberingStyle | PfmNumberingStart | PfmNumberingTab);
            paragraphs.Alignment = 1;
            paragraphs.LineSpacingRule = 0;
            paragraphs.NumberingStart = 1;
            SetParagraphs(box, ref paragraphs);
        }

        private static uint StyleMask(FontStyle style)
        {
            if ((style & ~(FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout)) != 0)
                throw new ArgumentOutOfRangeException("style");
            return (uint)style & (CfmBold | CfmItalic | CfmUnderline | CfmStrikeout);
        }

        private static CharacterFormat NewCharacterFormat(uint mask)
        {
            return new CharacterFormat { Size = (uint)Marshal.SizeOf(typeof(CharacterFormat)), Mask = mask, FaceName = "" };
        }
        private static ParagraphFormat NewParagraphFormat(uint mask)
        {
            return new ParagraphFormat { Size = (uint)Marshal.SizeOf(typeof(ParagraphFormat)), Mask = mask, Tabs = new int[32] };
        }
        private static CharacterFormat GetCharacters(RichTextBox box)
        {
            RequireControl(box);
            CharacterFormat format = NewCharacterFormat(0);
            SendCharacterFormat(box.Handle, EmGetCharFormat, new IntPtr(ScfSelection), ref format);
            return format;
        }
        private static ParagraphFormat GetParagraphs(RichTextBox box)
        {
            RequireControl(box);
            ParagraphFormat format = NewParagraphFormat(0);
            SendParagraphFormat(box.Handle, EmGetParaFormat, IntPtr.Zero, ref format);
            return format;
        }
        private static void SetCharacters(RichTextBox box, ref CharacterFormat format)
        {
            RequireControl(box);
            if (SendCharacterFormat(box.Handle, EmSetCharFormat, new IntPtr(ScfSelection), ref format) == IntPtr.Zero)
                throw new InvalidOperationException("The text engine could not apply this character format.");
        }
        private static void SetParagraphs(RichTextBox box, ref ParagraphFormat format)
        {
            RequireControl(box);
            if (SendParagraphFormat(box.Handle, EmSetParaFormat, IntPtr.Zero, ref format) == IntPtr.Zero)
                throw new InvalidOperationException("The text engine could not apply this paragraph format.");
        }
        private static void RequireControl(RichTextBox box)
        {
            if (box == null) throw new ArgumentNullException("box");
            if (box.IsDisposed || box.Disposing) throw new ObjectDisposedException("RichTextBox");
            if (box.InvokeRequired) throw new InvalidOperationException("Text formatting must run on the editor's UI thread.");
        }
        private static string ValidateFamily(string family)
        {
            if (String.IsNullOrWhiteSpace(family)) throw new ArgumentException("Choose a font family.", "family");
            family = family.Trim();
            if (family.Length > 31 || family.IndexOf('\0') >= 0) throw new ArgumentException("This font family name is not supported by the Windows text engine.", "family");
            return family;
        }
        private static int ColorRef(Color color) { return color.R | (color.G << 8) | (color.B << 16); }
        private static void ValidateFiniteRange(float value, float minimum, float maximum, string name)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
        }
    }
}
