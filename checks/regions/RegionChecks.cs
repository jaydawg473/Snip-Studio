using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using SnipStudio;

internal static class RegionChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception("FAILED: " + message);
    }
    private static TextWordRegion Word(string text, float x, float y, float width, float height)
    { return new TextWordRegion { Text = text, Bounds = new RectangleF(x, y, width, height) }; }
    private static void Paper(Graphics g, int x, int y)
    {
        using (Pen pen = new Pen(Color.Red))
        {
            g.DrawLine(pen,x+1,y,x+9,y);
            g.DrawLine(pen,x,y+1,x,y+13);
            g.DrawLine(pen,x+10,y+1,x+10,y+2);
            g.DrawLine(pen,x+11,y+3,x+11,y+13);
            g.DrawLine(pen,x+1,y+14,x+10,y+14);
            g.DrawLines(pen,new Point[] { new Point(x+3,y+10),new Point(x+6,y+4),new Point(x+8,y+10),
                new Point(x+3,y+10),new Point(x+8,y+7) });
        }
    }
    private static void BlueApp(Graphics g,int x,int y)
    {
        g.FillRectangle(Brushes.RoyalBlue,x+2,y,12,16);
        g.FillRectangle(Brushes.RoyalBlue,x,y+2,16,12);
        g.DrawRectangle(Pens.White,x+4,y+4,7,7);
    }
    private static TextRecognitionResult Seed(Bitmap bitmap, string prefix, bool paper, bool app, bool standalone, int count)
    {
        TextRecognitionResult result = new TextRecognitionResult { Available = true };
        using (Graphics g = Graphics.FromImage(bitmap))
        using (Font font = new Font("Segoe UI",12,FontStyle.Regular,GraphicsUnit.Pixel))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            for (int i=0;i<count;i++)
            {
                int y=20+i*32;
                bool appRow = app && i == count-1;
                string token = appRow ? "a" : prefix;
                if (paper && !appRow) Paper(g,18,y);
                else if (appRow) BlueApp(g,17,y);
                else g.DrawString(token,font,Brushes.Red,18,y);
                TextLineRegion line = new TextLineRegion();
                if (token != null) line.Words.Add(Word(token,appRow?17:18,y,appRow?16:12,15));
                if (!(standalone && i == count-1))
                {
                    string filename = "Manual-Update-1.7." + (12+i) + ".pdf";
                    g.DrawString(filename,font,Brushes.Black,36,y+1);
                    line.Words.Add(Word(filename,36,y+3,220,12));
                }
                List<string> words = new List<string>();
                RectangleF bounds = RectangleF.Empty;
                foreach (TextWordRegion word in line.Words)
                {
                    words.Add(word.Text);
                    bounds=bounds.IsEmpty?word.Bounds:RectangleF.Union(bounds,word.Bounds);
                }
                line.Text=String.Join(" ",words.ToArray());
                line.Bounds=bounds;
                result.Lines.Add(line);
            }
        }
        List<string> lines = new List<string>();
        foreach(TextLineRegion line in result.Lines) lines.Add(line.Text);
        result.Text=String.Join(Environment.NewLine,lines.ToArray());
        return result;
    }
    private static void SyntheticChecks()
    {
        using(Bitmap image = new Bitmap(520,180,PixelFormat.Format32bppArgb))
        {
            TextRecognitionResult result=Seed(image,"@",true,false,false,3);
            string raw=result.Text;
            int iconPixel=image.GetPixel(18,22).ToArgb();
            RecognizedTextRegions.Refine(result,image);
            Check(result.RawText == raw,"verbatim native OCR retained");
            Check(result.Lines.Count == 3,"three PDF rows retained");
            foreach(TextLineRegion line in result.Lines)
            {
                Check(line.Words.Count == 1 && !line.Text.StartsWith("@"),"PDF-only icon pseudo-word removed");
                Check(line.Bounds.Left == 36 && line.Bounds.Height == 12,"font/color bounds exclude red icon");
                Check(line.ExcludedGraphicBounds == new RectangleF(18,line.Bounds.Top-3,12,15),
                    "removed icon bounds are retained for exact padding protection");
            }
            Check(image.GetPixel(18,22).ToArgb() == iconPixel,"source icon pixels unchanged");
            result=Seed(image,null,true,false,false,3);
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines[0].ExcludedGraphicBounds == new RectangleF(18,20,12,15),
                "omitted native icon remains protected by its exact raster bounds");
            result=Seed(image,"@",true,false,false,3);
            result.Lines[0].ExcludedGraphicBounds=new RectangleF(14,20,2,15);
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines[0].ExcludedGraphicBounds == new RectangleF(14,20,16,15),
                "new excluded icon bounds union with existing protected graphics");
            result=Seed(image,"@",true,false,false,3);
            result.Lines[0].Words[1].Bounds=new RectangleF(31,23,220,12);
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines[0].Bounds.Left == 31 && result.Lines[0].ExcludedGraphicBounds.Right == 30,
                "one-pixel icon gap retains exact text and protected icon bounds");
            result=Seed(image,null,true,false,false,3);
            result.Lines[0].Words[0].Bounds=new RectangleF(31,23,220,12);
            result.Lines[0].Bounds=result.Lines[0].Words[0].Bounds;
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines[0].ExcludedGraphicBounds.Right == 30,
                "omitted icon remains protected across a one-pixel gap");
            result=Seed(image,"@",true,false,false,3);
            result.Lines[0].ExcludedGraphicBounds=new RectangleF(14,20,2,15);
            result.Lines[0].Words.Add(Word("secondary",420,23,80,12));
            result.Lines[0].Text+=" secondary";
            result.Lines[0].Bounds=RectangleF.Union(result.Lines[0].Bounds,new RectangleF(420,23,80,12));
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines.Count == 4 && result.Lines[0].ExcludedGraphicBounds.Left == 14,
                "split first text column retains existing graphic exclusions");
            Check(result.Lines[1].Text == "secondary" && result.Lines[1].ExcludedGraphicBounds.IsEmpty,
                "neighboring split text column does not inherit unrelated graphics");
            result=Seed(image,"@",true,true,false,3);
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines[2].Words.Count == 1 && !result.Lines[2].Text.StartsWith("a "),
                "single-hue application icon excluded using proven paper column");
            result=Seed(image,"@",true,false,true,3);
            RecognizedTextRegions.Refine(result,image);
            Check(result.Lines.Count == 2,"standalone proven icon pseudo-line removed");
            result=Seed(image,"@",true,false,false,1);
            RecognizedTextRegions.Refine(result,image);
            Check(result.Text.StartsWith("@ "),"isolated compact symbol retained without repeated-row evidence");
            result=Seed(image,"@",false,false,false,3);
            string genuine=result.Text;
            RecognizedTextRegions.Refine(result,image);
            Check(result.Text == genuine,"repeated genuine red at-signs remain text");
            result=Seed(image,"@",true,false,false,3);
            using(Graphics g=Graphics.FromImage(image))
            using(Font font=new Font("Segoe UI",12,FontStyle.Regular,GraphicsUnit.Pixel))
            {
                g.FillRectangle(Brushes.White,16,82,18,22);
                g.DrawString("@",font,Brushes.Red,18,84);
            }
            RecognizedTextRegions.Refine(result,image);
            Check(!result.Lines[0].Text.StartsWith("@ ") && result.Lines[2].Text.StartsWith("@ "),
                "real colored at-sign retained even beside an established icon column");
            result=Seed(image,"@filename",false,false,false,3);
            genuine=result.Text;
            RecognizedTextRegions.Refine(result,image);
            Check(result.Text == genuine,"leading at-sign inside identifier token remains exact");
            result=Seed(image,"N",false,false,false,3);
            genuine=result.Text;
            RecognizedTextRegions.Refine(result,image);
            Check(result.Text == genuine,"repeated genuine leading identifier letters remain text");
            result=Seed(image,"\u7530",false,false,false,3);
            genuine=result.Text;
            RecognizedTextRegions.Refine(result,image);
            Check(result.Text == genuine,"compact non-Latin leading identifiers remain exact");
        }
    }
    private static void NativeChecks(string privateImage)
    {
        if(String.IsNullOrEmpty(privateImage)) return;
        // A provided screenshot may already contain damaged rendered text.
        // Reuse only the untouched 12x15 PDF icon with invented filenames.
        using(Bitmap provided=new Bitmap(privateImage))
        using(Bitmap image=new Bitmap(520,200,PixelFormat.Format32bppArgb))
        using(Bitmap icon=provided.Clone(new Rectangle(59,101,12,15),PixelFormat.Format32bppArgb))
        {
            using(Graphics g=Graphics.FromImage(image))
            using(Font font=new Font("Segoe UI",12,FontStyle.Regular,GraphicsUnit.Pixel))
            {
                g.Clear(Color.White);
                g.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
                for(int i=0;i<4;i++)
                {
                    int y=20+i*36;
                    g.DrawImageUnscaled(icon,18,y);
                    g.DrawString("Manual-Update-1.7."+(12+i)+".pdf",font,Brushes.Black,34,y+1);
                }
            }
            TextRecognitionResult result=TextRecognition.RecognizeAsync(image,CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Available,"Windows OCR available for real-icon regression");
            Check(result.Lines.Count >= 4,"Windows OCR detects four generic PDF filenames");
            Check(result.RawText.Contains("@"),"real PDF icon produces the reported @ OCR failure before refinement");
            Check(!result.Text.Contains("@"),"production OCR/refinement excludes all real PDF @ pseudo-words");
            foreach(TextLineRegion line in result.Lines)
                Check(line.Bounds.Left >= 30,"native recognized editable bounds exclude untouched PDF icon pixels");
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            SyntheticChecks();
            NativeChecks(args.Length>0?args[0]:null);
            Console.WriteLine("PASS: "+checks+" icon-region assertions; real Windows OCR="+(args.Length>0));
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }
}
