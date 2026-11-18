using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using SnipStudio;

internal static class RecoveryChecks
{
    private static int assertions;
    private static void Check(bool value,string label)
    { assertions++;if(!value)throw new Exception("FAILED: "+label); }
    private static TextRecognitionResult Recognize(Bitmap image)
    { return TextRecognition.RecognizeAsync(image,CancellationToken.None).GetAwaiter().GetResult(); }
    private static TextLineRegion Observation(string text,float left,float top,float width)
    {
        TextLineRegion line=new TextLineRegion {Text=text,Bounds=new RectangleF(left,top,width,12)};
        line.Words.Add(new TextWordRegion {Text=text,Bounds=line.Bounds});
        return line;
    }
    private static void FilenameReadingOrder()
    {
        TextRecognitionResult primary=new TextRecognitionResult {Available=true};
        primary.Lines.Add(Observation("(1).pdf",246,40,34));
        primary.Lines.Add(Observation("(1).pdf",246,96,34));
        primary.Lines.Add(Observation("(4).pdf",240,152,34));
        // The original provider returns the filename column before date/type
        // columns. Recovery must retain this order while filling missing rows.
        TextLineRegion date1=Observation("11/18/2026",420,12,110);
        TextLineRegion date2=Observation("11/19/2026",420,40,110);
        primary.Lines.Add(date1);primary.Lines.Add(date2);
        TextRecognitionResult candidate=new TextRecognitionResult {Available=true};
        int[] widths={254,274,260,274,258,268};
        for(int row=0;row<6;row++)candidate.Lines.Add(Observation("Native-filename-0000"+row+".pdf",6,12+row*28,widths[row]));
        Check(RecognizedTextRecovery.MergeMissing(primary,candidate,new Rectangle(0,0,620,180),CancellationToken.None)==6,
            "six varying-width filename observations are accepted");
        for(int row=0;row<6;row++)Check(primary.Lines[row].Bounds.Top==12+row*28,
            "filename reading order retains source row "+(row+1));
        Check(Object.ReferenceEquals(primary.Lines[6],date1)&&Object.ReferenceEquals(primary.Lines[7],date2),
            "date column remains after all filename rows in provider order");
        Check(primary.Lines[0].Text.EndsWith("0.pdf")&&primary.Lines[1].Text.EndsWith("1.pdf"),
            "first-to-second filename selection encounters only those two rows");
    }
    private static void CheckRows(string path,bool explorer)
    {
        using(Bitmap image=new Bitmap(path))
        {
            int[] rows=explorer?new int[]{0,1,2,4,5,6,7,8,9}:new int[]{0,1,2,3,4,5};
            TextRecognitionResult result=Recognize(image);
            Check(result.Available,"provided input has available native OCR");
            Check(result.RecoveryPasses>=1&&result.RecoveryPasses<=2,"bounded one or two original-resolution recovery passes");
            Check(result.WasResized,"returned result honestly retains initial resized-pass flag");
            Check(!String.IsNullOrEmpty(result.RawText)&&!String.IsNullOrEmpty(result.RecoveryRawText),"initial and recovery native outputs retained separately");
            foreach(int row in rows)
            {
                int top=explorer?10+row*28:8+row*28;
                int count=0;
                foreach(TextLineRegion line in result.Lines)
                {
                    if(line.Bounds.Top>=top&&line.Bounds.Top<top+20&&line.Bounds.Left<(explorer?140:20)&&line.Bounds.Width>170)
                        count++;
                }
                Check(count==1,"one complete filename body at source row "+row+" for "+image.Width+"x"+image.Height);
                if(!explorer)Check(result.Lines[row].Bounds.Top>=top&&result.Lines[row].Bounds.Top<top+20,
                    "provided filename source row "+row+" remains at its correct reading-order index");
            }
            foreach(TextLineRegion line in result.Lines)
                Check(new RectangleF(PointF.Empty,image.Size).Contains(line.Bounds),"all recovered lines stay in source image coordinates");
            Console.WriteLine("Fixture "+image.Width+"x"+image.Height+": "+rows.Length+"/"+rows.Length+" bodies; passes="+result.RecoveryPasses+"; lines="+result.Lines.Count);
        }
    }
    private static void Generated()
    {
        using(Bitmap bitmap=new Bitmap(620,180,PixelFormat.Format32bppArgb))
        {
            using(Graphics g=Graphics.FromImage(bitmap))
            using(Font font=new Font("Segoe UI",20,FontStyle.Regular,GraphicsUnit.Pixel))
            {
                g.Clear(Color.White);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                g.DrawString("Part 00123-A  Keep repeated rows",font,Brushes.Black,14,16);
                g.DrawString("Part 00123-A  Keep repeated rows",font,Brushes.Black,14,62);
                g.DrawString("Text is available for editing",font,Brushes.DarkBlue,14,108);
            }
            TextRecognitionResult result=Recognize(bitmap);
            Check(result.Available&&result.Lines.Count==3,"ordinary generated text keeps three independent rows");
            int repeated=0;
            foreach(TextLineRegion line in result.Lines)if(line.Text.Contains("00123-A"))repeated++;
            Check(repeated==2,"clear generated identifier and its legitimate duplicate retain leading zeros and hyphen");
            for(int first=0;first<result.Lines.Count;first++)for(int second=first+1;second<result.Lines.Count;second++)
                Check(!result.Lines[first].Bounds.IntersectsWith(result.Lines[second].Bounds),"recovery introduces no overlapping duplicate text rows");
            using(CancellationTokenSource canceled=new CancellationTokenSource())
            {
                canceled.Cancel();bool observed=false;
                try{TextRecognition.RecognizeAsync(bitmap,canceled.Token).GetAwaiter().GetResult();}
                catch(OperationCanceledException){observed=true;}
                Check(observed,"caller cancellation remains cancellation");
            }
        }
    }
    public static int Main(string[] args)
    {
        try
        {
            Generated();
            FilenameReadingOrder();
            if(args.Length>0)CheckRows(args[0],false);
            if(args.Length>1)CheckRows(args[1],true);
            Console.WriteLine("PASS: "+assertions+" OCR-recovery assertions; source pixels and OCR characters are not rewritten.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
