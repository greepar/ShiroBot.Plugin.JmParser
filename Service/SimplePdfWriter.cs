using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ShiroBot.JmParser.Service;

internal static class SimplePdfWriter
{
    public static Task WriteAsync(IReadOnlyList<PdfImagePage> pages, string pdfPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);

        using var document = new PdfDocument();
        foreach (var imagePage in pages)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(imagePage.Width);
            page.Height = XUnit.FromPoint(imagePage.Height);

            using var gfx = XGraphics.FromPdfPage(page);
            using var image = XImage.FromFile(imagePage.Path);
            gfx.DrawImage(image, 0, 0, imagePage.Width, imagePage.Height);
        }

        document.Save(pdfPath);
        return Task.CompletedTask;
    }
}
