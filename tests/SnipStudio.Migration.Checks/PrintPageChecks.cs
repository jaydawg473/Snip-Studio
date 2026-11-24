using System;
using System.Drawing;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckPrintPageGeometry()
    {
        var userMargins = new RectangleF(36, 36, 778, 1028);
        var printableArea = new RectangleF(60, 30, 740, 1020);
        RectangleF wide = DesktopPrintingService.CalculateDestination(new Size(1600, 800), userMargins, printableArea);
        Check(wide == new RectangleF(60, 358, 740, 370), "A wide snip fits the printer's actual width and is centered in the safe page area.");

        RectangleF tall = DesktopPrintingService.CalculateDestination(new Size(800, 1600), userMargins, printableArea);
        Check(tall == new RectangleF(176.5f, 36, 507, 1014), "A tall snip fits the printer's actual height and is centered in the safe page area.");

        RectangleF letterPaper = DesktopPrintingService.FitInside(new Size(850, 1100), new RectangleF(10, 20, 425, 800));
        Check(letterPaper == new RectangleF(10, 145, 425, 550), "Print preview fits and centers the full physical page without stretching it.");

        string? failure = null;
        try
        {
            DesktopPrintingService.CalculateDestination(new Size(200, 100), userMargins, new RectangleF(820, 30, 20, 20));
        }
        catch (InvalidOperationException error)
        {
            failure = error.Message;
        }
        Check(failure?.Contains("Page setup", StringComparison.Ordinal) == true, "An unprintable area blocks the job and explains how to recover.");
    }
}
