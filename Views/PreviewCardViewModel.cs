using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using ShiroBot.JmParser.Service;

namespace ShiroBot.JmParser.Views;

public sealed class PreviewCardViewModel
{
    public PreviewCardViewModel()
    {
        AlbumId = "JM123456";
        Title = "这里是漫画标题";
        PageText = "共 128 页";
        ViewCount = "13K";
        LikeCount = "1K";
        CoverBlurRadius = 12;
        Cover = LoadDesignCover();
    }

    internal PreviewCardViewModel(PdfBuildResult result, double coverBlurRadius)
    {
        AlbumId = Path.GetFileNameWithoutExtension(result.FileName);
        Title = result.Title;
        PageText = result.PageCount > 0 ? $"共 {result.PageCount} 页" : "页数未知";
        ViewCount = FormatCount(result.ViewCount);
        LikeCount = FormatCount(result.LikeCount);
        CoverBlurRadius = coverBlurRadius;
        Cover = string.IsNullOrWhiteSpace(result.CoverPath) ? null : new Bitmap(result.CoverPath);
    }

    public string AlbumId { get; }

    public string Title { get; }

    public string PageText { get; }

    public string ViewCount { get; }

    public string LikeCount { get; }

    public Bitmap? Cover { get; }

    public double CoverBlurRadius { get; }

    private static string FormatCount(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static Bitmap? LoadDesignCover([CallerFilePath] string sourceFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Assets", "shiroka.png"));
        return File.Exists(path) ? new Bitmap(path) : null;
    }
}
