using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShiroBot.JmParser.Service;

internal static class CommandParser
{
    private const string Command = "#jm";

    public static bool TryParseAlbumId(string text, out string albumId)
    {
        albumId = string.Empty;
        if (!text.StartsWith(Command, StringComparison.OrdinalIgnoreCase)) return false;

        var payload = text[Command.Length..].Trim();
        var match = Regex.Match(payload, @"(?:albums?|photos?)/(?<id>\d+)|id=(?<id>\d+)|(?:jm)?(?<id>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        albumId = match.Groups["id"].Value;
        return albumId.Length > 0;
    }

    public static string TrimError(string message) => message.Length <= 300 ? message : message[..300] + "...";
}

internal enum OutputMode
{
    File,
    Url,
    Both
}

internal static class OutputModeParser
{
    public static OutputMode Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "url" => OutputMode.Url,
            "both" => OutputMode.Both,
            "file" or null or "" => OutputMode.File,
            _ => throw new InvalidOperationException("OutputMode 只能配置为 file、url 或 both。")
        };
    }
}

internal sealed class JmPdfBuilder(JmComicDownloader downloader, string dataDir)
{
    public async Task<string> FetchAlbumTitleAsync(string albumId)
    {
        var (html, _) = await downloader.GetAlbumHtmlAsync(albumId).ConfigureAwait(false);
        var album = JmHtmlParser.ParseAlbum(html, albumId);
        return album.Title;
    }

    public async Task<PdfBuildResult> BuildAsync(string albumId)
    {
        var workDir = Path.Combine(dataDir, albumId);
        var pdfPath = Path.Combine(workDir, $"JM{albumId}.pdf");

        if (File.Exists(pdfPath))
        {
            return new PdfBuildResult(pdfPath, Path.GetFileName(pdfPath));
        }

        var pageDir = Path.Combine(workDir, "pages");
        Directory.CreateDirectory(pageDir);

        var album = await downloader.DownloadAlbumAsync(albumId, pageDir).ConfigureAwait(false);
        if (album.Pages.Count == 0)
        {
            throw new InvalidOperationException("没有下载到任何图片。可能是域名不可用、地区限制或页面结构已变更。");
        }

        await SimplePdfWriter.WriteAsync(album.Pages, pdfPath).ConfigureAwait(false);
        Directory.SetLastWriteTimeUtc(workDir, DateTime.UtcNow);

        return new PdfBuildResult(pdfPath, Path.GetFileName(pdfPath));
    }
}

internal static class JmRetentionCleaner
{
    public static void Cleanup(string dataDir, TimeSpan retention)
    {
        if (retention.TotalMinutes <= 0 || !Directory.Exists(dataDir)) return;

        var cutoff = DateTime.UtcNow.Subtract(retention);
        foreach (var directory in Directory.EnumerateDirectories(dataDir))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup races with active downloads or preview reads.
            }
        }
    }
}

internal sealed class JmComicDownloader : IDisposable
{
    private static readonly Regex DomainPattern = new(@"[\w-]+\.\w+(?:/[\w-]+)?", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly int _maxConcurrency;
    private List<string>? _domains;

    public JmComicDownloader(string? proxy, int maxConcurrency)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            MaxConnectionsPerServer = maxConcurrency * 2,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy);
        }

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        _maxConcurrency = maxConcurrency;
    }

    public async Task<(string Html, string Url)> GetAlbumHtmlAsync(string albumId)
    {
        return await GetHtmlAsync($"/album/{albumId}").ConfigureAwait(false);
    }

    public async Task<JmDownloadedAlbum> DownloadAlbumAsync(string albumId, string pageDir)
    {
        var albumPage = await GetHtmlAsync($"/album/{albumId}").ConfigureAwait(false);
        var album = JmHtmlParser.ParseAlbum(albumPage.Html, albumId);

        // 第一步：并发获取所有章节的 photo 页面
        var chapterPhotoTasks = album.Chapters
            .Select(chapter => FetchPhotoAsync(chapter, album.ScrambleId))
            .ToArray();

        var chapterPhotos = await Task.WhenAll(chapterPhotoTasks).ConfigureAwait(false);

        // 第二步：并发下载所有图片，按顺序组装
        var pending = new List<(int ChapterIndex, int PageIndex, string SavePath, string Url, string Referer, string ScrambleId, string PhotoId, string ImageName)>();

        foreach (var (chapter, photo, pageUrl) in chapterPhotos)
        {
            for (var i = 0; i < photo.ImageUrls.Count; i++)
            {
                var imageUrl = photo.ImageUrls[i];
                var imageName = GetImageFileName(imageUrl, i + 1);
                var savePath = Path.Combine(pageDir, $"{chapter.Index:000}-{i + 1:00000}.jpg");

                if (!File.Exists(savePath))
                {
                    pending.Add((0, i, savePath, imageUrl, pageUrl, photo.ScrambleId, photo.PhotoId, imageName));
                }
            }
        }

        // 并发下载
        var semaphore = new SemaphoreSlim(_maxConcurrency);
        var downloadTasks = pending.Select(async item =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = await GetBytesAsync(item.Url, item.Referer).ConfigureAwait(false);
                var num = JmImageDecoder.CalculateScrambleNum(item.ScrambleId, item.PhotoId, Path.GetFileNameWithoutExtension(item.ImageName));
                JmImageDecoder.DecodeAndSaveJpeg(bytes, num, item.SavePath);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);

        // 读取所有 page 尺寸，保持原始章节顺序
        var pages = new List<PdfImagePage>();
        foreach (var (chapter, photo, _) in chapterPhotos)
        {
            for (var i = 0; i < photo.ImageUrls.Count; i++)
            {
                var savePath = Path.Combine(pageDir, $"{chapter.Index:000}-{i + 1:00000}.jpg");
                var info = ImageInfo.Read(savePath);
                pages.Add(new PdfImagePage(savePath, info.Width, info.Height));
            }
        }

        return new JmDownloadedAlbum(album.AlbumId, album.Title, pages);
    }

    private async Task<(ChapterDetail Chapter, PhotoDetail Photo, string PageUrl)> FetchPhotoAsync(ChapterDetail chapter, string albumScrambleId)
    {
        var photoPage = await GetHtmlAsync($"/photo/{chapter.PhotoId}").ConfigureAwait(false);
        var photo = JmHtmlParser.ParsePhoto(photoPage.Html, chapter, albumScrambleId, photoPage.Url);
        return (chapter, photo, photoPage.Url);
    }

    public void Dispose() => _http.Dispose();

    private static readonly HashSet<string> BlockedHosts =
    [
        "t.me", "telegram.org", "telegram.me"
    ];

    private async Task<(string Html, string Url)> GetHtmlAsync(string path)
    {
        var errors = new List<string>();
        foreach (var domain in await GetDomainsAsync().ConfigureAwait(false))
        {
            var url = $"https://{domain.TrimEnd('/')}{path}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://18comic.vip/");
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;

                var finalHost = new Uri(finalUrl).Host;
                if (BlockedHosts.Contains(finalHost))
                {
                    errors.Add($"{domain} → {finalHost}: 重定向到非 JM 站点，跳过");
                    continue;
                }

                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"{domain}: {(int)resp.StatusCode}");
                    continue;
                }

                if (text.Contains("Restricted Access!", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{domain}: Restricted Access");
                    continue;
                }

                var decoded = JmHtmlParser.DecodeBase64Html(text);

                if (!IsJmHtml(decoded))
                {
                    errors.Add($"{domain}: 响应内容不包含 JM 特征标记（scramble_id/page_arr），跳过");
                    continue;
                }

                return (decoded, finalUrl);
            }
            catch (Exception ex)
            {
                errors.Add($"{domain}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("所有 JM 网页域名都请求失败: " + string.Join("; ", errors));
    }

    private static bool IsJmHtml(string html)
    {
        return html.Contains("scramble_id", StringComparison.OrdinalIgnoreCase)
               || html.Contains("page_arr", StringComparison.OrdinalIgnoreCase)
               || html.Contains("18comic", StringComparison.OrdinalIgnoreCase)
               || html.Contains("JMComic", StringComparison.OrdinalIgnoreCase);
    }

    private const int MaxImageRetries = 3;
    private const int SlowSpeedThreshold = 10 * 1024; // 10 KB/s
    private const int SlowDurationSeconds = 3;

    private async Task<byte[]> GetBytesAsync(string url, string referer)
    {
        for (var attempt = 1; attempt <= MaxImageRetries; attempt++)
        {
            try
            {
                return await DownloadWithSpeedCheckAsync(url, referer).ConfigureAwait(false);
            }
            catch (SlowDownloadException) when (attempt < MaxImageRetries)
            {
                // Speed too slow; retry
            }
            catch (HttpRequestException) when (attempt < MaxImageRetries)
            {
                // Transient network error; retry
            }
            catch (TaskCanceledException) when (attempt < MaxImageRetries)
            {
                // Timeout; retry
            }
        }

        throw new InvalidOperationException($"下载失败，已重试 {MaxImageRetries} 次: {url}");
    }

    private async Task<byte[]> DownloadWithSpeedCheckAsync(string url, string referer)
    {
        using var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var monitorCts = new CancellationTokenSource();

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        req.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false);

        var contentLength = resp.Content.Headers.ContentLength ?? -1;
        await using var ms = new MemoryStream(contentLength > 0 ? (int)contentLength : 8192);

        var buffer = new byte[81920];
        var speedCheck = MonitorSpeedAsync(ms, monitorCts.Token, () => downloadCts.Cancel());
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, downloadCts.Token).ConfigureAwait(false)) > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, bytesRead), downloadCts.Token).ConfigureAwait(false);
            }

            monitorCts.Cancel();
            await AwaitMonitorShutdownAsync(speedCheck).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (speedCheck.IsFaulted && HasSlowDownloadException(speedCheck.Exception))
        {
            throw new SlowDownloadException("下载速度低于 10KB/s 超过 3 秒");
        }
        catch
        {
            monitorCts.Cancel();
            await AwaitMonitorShutdownAsync(speedCheck).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task MonitorSpeedAsync(MemoryStream ms, CancellationToken ct, Action cancelDownload)
    {
        var lastPosition = 0L;
        var slowStart = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            var currentPosition = ms.Position;
            var bytesInSecond = currentPosition - lastPosition;
            lastPosition = currentPosition;

            if (bytesInSecond < SlowSpeedThreshold)
            {
                if (slowStart == DateTime.MinValue)
                {
                    slowStart = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - slowStart).TotalSeconds >= SlowDurationSeconds)
                {
                    cancelDownload();
                    throw new SlowDownloadException("下载速度低于 10KB/s 超过 3 秒");
                }
            }
            else
            {
                slowStart = DateTime.MinValue;
            }
        }
    }

    private static async Task AwaitMonitorShutdownAsync(Task speedCheck)
    {
        try
        {
            await speedCheck.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation after the image finished downloading.
        }
    }

    private static bool HasSlowDownloadException(AggregateException? exception) =>
        exception?.Flatten().InnerExceptions.Any(ex => ex is SlowDownloadException) == true;

    private sealed class SlowDownloadException(string message) : Exception(message);

    private async Task<IReadOnlyList<string>> GetDomainsAsync()
    {
        if (_domains is not null) return _domains;

        var domains = new List<string>();
        await TryAddRedirectDomainAsync(domains).ConfigureAwait(false);
        await TryAddPublishPageDomainsAsync(domains).ConfigureAwait(false);

        AddDomain(domains, "18comic.vip");
        AddDomain(domains, "18comic.org");
        AddDomain(domains, "jmcomicgo.org");

        _domains = domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return _domains;
    }

    private async Task TryAddRedirectDomainAsync(List<string> domains)
    {
        try
        {
            using var resp = await _http.GetAsync("https://jm365.work/3YeBdF").ConfigureAwait(false);
            var host = resp.RequestMessage?.RequestUri?.Host;
            if (string.IsNullOrWhiteSpace(host) || BlockedHosts.Contains(host)) return;
            AddDomain(domains, host);
        }
        catch
        {
            // The fixed fallback domains below will still be tried.
        }
    }

    private async Task TryAddPublishPageDomainsAsync(List<string> domains)
    {
        try
        {
            var html = await _http.GetStringAsync("https://jmcomicgo.org").ConfigureAwait(false);
            foreach (Match match in DomainPattern.Matches(html))
            {
                var value = match.Value;
                if (value.Contains("jm", StringComparison.OrdinalIgnoreCase) || value.Contains("comic", StringComparison.OrdinalIgnoreCase))
                {
                    AddDomain(domains, value.Split('/')[0]);
                }
            }
        }
        catch
        {
            // The fixed fallback domains below will still be tried.
        }
    }

    private static void AddDomain(List<string> domains, string domain)
    {
        domain = domain.Trim().Trim('/');
        if (domain.Length != 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) domains.Add(domain);
    }

    private static string GetImageFileName(string imageUrl, int index)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) return Path.GetFileName(uri.AbsolutePath);
        return $"{index:00000}.jpg";
    }
}

internal static class JmHtmlParser
{
    private static readonly Regex Base64HtmlPattern = new("const html = base64DecodeUtf8\\(\"(?<html>.*?)\"\\)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AlbumIdPattern = new("<span class=\"number\">.*?：JM(?<id>\\d+)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AlbumTitlePattern = new("id=\"book-name\"[^>]*?>(?<title>[\\s\\S]*?)<", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new("data-album=\"(?<id>\\d+)\"[^>]*>[\\s\\S]*?第(?<index>\\d+)[话話](?<title>[\\s\\S]*?)<", RegexOptions.Compiled);
    private static readonly Regex PhotoIdPattern = new("<meta property=\"og:url\" content=\".*?/photo/(?<id>\\d+)/?.*?\">", RegexOptions.Compiled);
    private static readonly Regex ScramblePattern = new("var scramble_id = (?<id>\\d+);", RegexOptions.Compiled);
    private static readonly Regex PageArrayPattern = new("var page_arr = (?<json>.*?);", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageDomainPattern = new("src=\"https://(?<domain>.*?)/media/albums/blank", RegexOptions.Compiled);
    private static readonly Regex FirstOriginalPattern = new("data-original=\"(?<url>.*?)\"[^>]*?id=\"album_photo[^>]*?data-page=\"0\"", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex OriginalPattern = new("data-original=\"(?<url>.*?)\"", RegexOptions.Compiled);

    public static AlbumDetail ParseAlbum(string html, string fallbackId)
    {
        var albumId = MatchOrDefault(AlbumIdPattern, html, "id") ?? fallbackId;
        var scrambleId = MatchOrDefault(ScramblePattern, html, "id") ?? "220980";
        var title = CleanHtml(MatchOrDefault(AlbumTitlePattern, html, "title") ?? $"JM{albumId}");
        var chapters = new List<ChapterDetail>();

        foreach (Match match in EpisodePattern.Matches(html))
        {
            var photoId = match.Groups["id"].Value;
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            var chapterTitle = CleanHtml(match.Groups["title"].Value);
            if (chapters.All(c => c.PhotoId != photoId)) chapters.Add(new ChapterDetail(photoId, index, chapterTitle));
        }

        if (chapters.Count == 0) chapters.Add(new ChapterDetail(albumId, 1, title));

        chapters.Sort((x, y) => x.Index.CompareTo(y.Index));
        return new AlbumDetail(albumId, scrambleId, title, chapters);
    }

    public static PhotoDetail ParsePhoto(string html, ChapterDetail chapter, string albumScrambleId, string pageUrl)
    {
        var photoId = MatchOrDefault(PhotoIdPattern, html, "id") ?? chapter.PhotoId;
        var scrambleId = MatchOrDefault(ScramblePattern, html, "id") ?? albumScrambleId;
        var firstOriginal = WebUtility.HtmlDecode(MatchOrDefault(FirstOriginalPattern, html, "url") ?? string.Empty);
        var imageUrls = ParseImageUrlsFromPageArray(html, photoId, firstOriginal);

        if (imageUrls.Count == 0)
        {
            foreach (Match match in OriginalPattern.Matches(html))
            {
                var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(url) && imageUrls.Contains(url) == false) imageUrls.Add(url);
            }
        }

        if (imageUrls.Count == 0) throw new InvalidOperationException($"章节 {chapter.PhotoId} 未解析到图片列表: {pageUrl}");

        return new PhotoDetail(photoId, scrambleId, imageUrls);
    }

    public static string DecodeBase64Html(string html)
    {
        var match = Base64HtmlPattern.Match(html);
        if (!match.Success) return html;

        var bytes = Convert.FromBase64String(match.Groups["html"].Value);
        return Encoding.UTF8.GetString(bytes);
    }

    private static List<string> ParseImageUrlsFromPageArray(string html, string photoId, string firstOriginal)
    {
        var imageUrls = new List<string>();
        var arrayJson = MatchOrDefault(PageArrayPattern, html, "json");
        if (string.IsNullOrWhiteSpace(arrayJson)) return imageUrls;

        var pageArr = JsonSerializer.Deserialize<List<string>>(arrayJson) ?? [];
        var domain = MatchOrDefault(ImageDomainPattern, html, "domain");
        if (string.IsNullOrWhiteSpace(domain) && Uri.TryCreate(firstOriginal, UriKind.Absolute, out var firstUri)) domain = firstUri.Host;
        if (string.IsNullOrWhiteSpace(domain)) return imageUrls;

        var query = ExtractQuery(firstOriginal);
        foreach (var imgName in pageArr)
        {
            var url = $"https://{domain}/media/photos/{photoId}/{imgName}";
            if (query.Length != 0) url += "?" + query;
            imageUrls.Add(url);
        }

        return imageUrls;
    }

    private static string ExtractQuery(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Query.TrimStart('?');

        var index = url.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? string.Empty : url[(index + 1)..];
    }

    private static string? MatchOrDefault(Regex regex, string text, string groupName)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[groupName].Value : null;
    }

    private static string CleanHtml(string value)
    {
        value = Regex.Replace(value, "<.*?>", string.Empty, RegexOptions.Singleline);
        value = WebUtility.HtmlDecode(value);
        return Regex.Replace(value, "\\s+", " ").Trim();
    }
}

internal static class JmImageDecoder
{
    public static int CalculateScrambleNum(string scrambleIdText, string aidText, string filename)
    {
        var scrambleId = int.Parse(scrambleIdText, CultureInfo.InvariantCulture);
        var aid = int.Parse(aidText, CultureInfo.InvariantCulture);

        if (aid < scrambleId) return 0;
        if (aid < 268850) return 10;

        var modBase = aid < 421926 ? 10 : 8;
        var input = Encoding.UTF8.GetBytes(aid + filename);
        var hash = Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
        var num = hash[^1] % modBase;
        return num * 2 + 2;
    }

    public static void DecodeAndSaveJpeg(byte[] bytes, int num, string savePath)
    {
        using var src = Image.Load<Rgba32>(bytes);
        src.Mutate(x => x.AutoOrient());

        using var decoded = num == 0 ? src.Clone() : DecodeScrambledImage(src, num);
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        decoded.SaveAsJpeg(savePath, new JpegEncoder { Quality = 92 });
    }

    private static Image<Rgba32> DecodeScrambledImage(Image<Rgba32> src, int num)
    {
        var width = src.Width;
        var height = src.Height;
        var decoded = new Image<Rgba32>(width, height, Color.White);
        var over = (int)(height % num);

        for (var i = 0; i < num; i++)
        {
            var move = (int)Math.Floor(height / (double)num);
            var ySrc = height - move * (i + 1) - over;
            var yDst = move * i;
            if (i == 0) move += over;
            else yDst += over;

            using var piece = src.Clone(x => x.Crop(new Rectangle(0, ySrc, width, move)));
            decoded.Mutate(x => x.DrawImage(piece, new Point(0, yDst), 1f));
        }

        return decoded;
    }
}

internal static class ImageInfo
{
    public static (uint Width, uint Height) Read(string path)
    {
        var info = Image.Identify(path);
        return ((uint)info.Width, (uint)info.Height);
    }
}

internal static class FileNameCleaner
{
    public static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "jmcomic" : cleaned;
    }
}

internal sealed record AlbumDetail(string AlbumId, string ScrambleId, string Title, List<ChapterDetail> Chapters);

internal sealed record ChapterDetail(string PhotoId, int Index, string Title);

internal sealed record PhotoDetail(string PhotoId, string ScrambleId, List<string> ImageUrls);

internal sealed record JmDownloadedAlbum(string AlbumId, string Title, IReadOnlyList<PdfImagePage> Pages);

internal sealed record PdfImagePage(string Path, uint Width, uint Height);

internal sealed record PdfBuildResult(string PdfPath, string FileName)
{
    public string? PreviewUrl { get; init; }
}
