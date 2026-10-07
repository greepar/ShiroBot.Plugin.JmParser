using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShiroBot.SDK.Abstractions;

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
    public async Task<PdfBuildResult> BuildAsync(string albumId)
    {
        var workDir = Path.Combine(dataDir, albumId);
        var pdfPath = Path.Combine(workDir, $"JM{albumId}.pdf");

        if (File.Exists(pdfPath))
        {
            var pages = FindDownloadedPages(workDir);
            try
            {
                var (html, _) = await downloader.GetAlbumHtmlAsync(albumId).ConfigureAwait(false);
                var cachedAlbum = JmHtmlParser.ParseAlbum(html, albumId);
                return CreateBuildResult(pdfPath, cachedAlbum, pages);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("所有 JM 网页域名都请求失败", StringComparison.Ordinal))
            {
                return CreateBuildResult(pdfPath, new AlbumDetail(albumId, "220980", $"JM{albumId}", null, null, []), pages);
            }
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

        return CreateBuildResult(pdfPath, album, album.Pages);
    }

    private static PdfBuildResult CreateBuildResult(string pdfPath, JmDownloadedAlbum album, IReadOnlyList<PdfImagePage> pages)
    {
        return new PdfBuildResult(
            pdfPath,
            Path.GetFileName(pdfPath),
            pages.FirstOrDefault()?.Path,
            album.Title,
            pages.Count,
            album.ViewCount,
            album.LikeCount);
    }

    private static PdfBuildResult CreateBuildResult(string pdfPath, AlbumDetail album, IReadOnlyList<PdfImagePage> pages)
    {
        return new PdfBuildResult(
            pdfPath,
            Path.GetFileName(pdfPath),
            pages.FirstOrDefault()?.Path,
            album.Title,
            pages.Count,
            album.ViewCount,
            album.LikeCount);
    }

    private static IReadOnlyList<PdfImagePage> FindDownloadedPages(string workDir)
    {
        var pageDir = Path.Combine(workDir, "pages");
        if (!Directory.Exists(pageDir)) return [];

        return Directory.EnumerateFiles(pageDir, "*.jpg")
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var info = ImageInfo.Read(path);
                return new PdfImagePage(path, info.Width, info.Height);
            })
            .ToArray();
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
    private static readonly string[] CdnImageDomains = ["cdn-msp.18comic.vip", "cdn-msp2.18comic.vip", "cdn-msp3.18comic.vip"];
    private static readonly string[] CdnImageSuffixes = [".webp", ".jpg", ".png", ".gif"];
    private const string DefaultScrambleId = "220980";
    private const int MaxCdnProbePages = 500;
    private const int MaxCdnConsecutiveMisses = 5;
    private const int CdnProbeBatchSize = 32;
    private const int HtmlDomainTimeoutSeconds = 12;
    private static readonly bool UseCdnDirectFirst = true;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _imageGate;
    private List<string>? _domains;

    public JmComicDownloader(string? proxy, int maxConcurrency)
    {
        maxConcurrency = Math.Clamp(maxConcurrency, 1, 64);
        _imageGate = new SemaphoreSlim(maxConcurrency);
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
    }

    internal JmComicDownloader(HttpClient http, int maxConcurrency)
    {
        _http = http;
        _imageGate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 64));
    }

    public async Task<(string Html, string Url)> GetAlbumHtmlAsync(string albumId)
    {
        return await GetHtmlAsync($"/album/{albumId}").ConfigureAwait(false);
    }

    public async Task<JmDownloadedAlbum> DownloadAlbumAsync(string albumId, string pageDir)
    {
        if (UseCdnDirectFirst)
        {
            return await DownloadCdnAlbumAsync(albumId, pageDir).ConfigureAwait(false);
        }

        AlbumDetail album;
        (ChapterDetail Chapter, PhotoDetail Photo, string PageUrl)[] chapterPhotos;
        try
        {
            var albumPage = await GetHtmlAsync($"/album/{albumId}").ConfigureAwait(false);
            album = JmHtmlParser.ParseAlbum(albumPage.Html, albumId);

            // 第一步：并发获取所有章节的 photo 页面
            var chapterPhotoTasks = album.Chapters
                .Select(chapter => FetchPhotoAsync(chapter, album.ScrambleId))
                .ToArray();

            chapterPhotos = await Task.WhenAll(chapterPhotoTasks).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("所有 JM 网页域名都请求失败", StringComparison.Ordinal))
        {
            BotLog.Info($"[JmParser] JM{albumId} 网页端不可用，开始使用 CDN 直连探测。");
            return await DownloadCdnAlbumAsync(albumId, pageDir).ConfigureAwait(false);
        }

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
        BotLog.Info($"[JmParser] JM{albumId} 已解析 {chapterPhotos.Sum(item => item.Photo.ImageUrls.Count)} 张图片，待下载 {pending.Count} 张。");
        var downloadTasks = pending.Select(item =>
        {
            var num = JmImageDecoder.CalculateScrambleNum(item.ScrambleId, item.PhotoId, Path.GetFileNameWithoutExtension(item.ImageName));
            return DownloadAndSaveImageAsync(item.Url, item.Referer, num, item.SavePath);
        });

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);
        BotLog.Info($"[JmParser] JM{albumId} 图片下载完成，开始生成 PDF。");

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

        return new JmDownloadedAlbum(album.AlbumId, album.Title, album.ViewCount, album.LikeCount, pages);
    }

    private async Task<(ChapterDetail Chapter, PhotoDetail Photo, string PageUrl)> FetchPhotoAsync(ChapterDetail chapter, string albumScrambleId)
    {
        var photoPage = await GetHtmlAsync($"/photo/{chapter.PhotoId}").ConfigureAwait(false);
        var photo = JmHtmlParser.ParsePhoto(photoPage.Html, chapter, albumScrambleId, photoPage.Url);
        return (chapter, photo, photoPage.Url);
    }

    private async Task<JmDownloadedAlbum> DownloadCdnAlbumAsync(string albumId, string pageDir)
    {
        Directory.CreateDirectory(pageDir);
        var albumDetailTask = TryGetAlbumDetailAsync(albumId);
        string? cdnDomain = null;
        string? suffix = null;
        var firstSaved = false;

        foreach (var domain in CdnImageDomains)
        {
            foreach (var candidateSuffix in CdnImageSuffixes)
            {
                var url = BuildCdnImageUrl(albumId, 1, domain, candidateSuffix);
                var savePath = Path.Combine(pageDir, "001-00001.jpg");
                if (!await TryDownloadAndSaveImageAsync(url, "https://18comic.vip/", albumId, savePath).ConfigureAwait(false)) continue;

                cdnDomain = domain;
                suffix = candidateSuffix;
                firstSaved = true;
                break;
            }

            if (firstSaved) break;
        }

        if (!firstSaved || cdnDomain is null || suffix is null)
        {
            throw new InvalidOperationException($"网页端无法访问，且 CDN 未找到 JM{albumId} 的第 1 张图片。");
        }

        var saved = new SortedSet<int> { 1 };
        for (var start = 2; start <= MaxCdnProbePages; start += CdnProbeBatchSize)
        {
            var end = Math.Min(start + CdnProbeBatchSize - 1, MaxCdnProbePages);
            var results = await Task.WhenAll(Enumerable.Range(start, end - start + 1).Select(async index =>
            {
                var url = BuildCdnImageUrl(albumId, index, cdnDomain, suffix);
                var savePath = Path.Combine(pageDir, $"001-{index:00000}.jpg");
                var ok = File.Exists(savePath) || await TryDownloadAndSaveImageAsync(url, "https://18comic.vip/", albumId, savePath).ConfigureAwait(false);
                return (Index: index, Ok: ok);
            }))
                .ConfigureAwait(false);

            foreach (var (index, ok) in results)
            {
                if (ok) saved.Add(index);
            }

            var consecutiveMisses = 0;
            for (var index = 1; index <= end; index++)
            {
                if (saved.Contains(index)) consecutiveMisses = 0;
                else consecutiveMisses++;

                if (consecutiveMisses >= MaxCdnConsecutiveMisses)
                {
                    return CreateCdnAlbumResult(albumId, await albumDetailTask.ConfigureAwait(false), pageDir, saved.Where(page => page < index - MaxCdnConsecutiveMisses + 1));
                }
            }

            BotLog.Info($"[JmParser] JM{albumId} CDN 已下载 {saved.Count} 张图片，当前进度 {end}/{MaxCdnProbePages}。");
        }

        return CreateCdnAlbumResult(albumId, await albumDetailTask.ConfigureAwait(false), pageDir, saved);
    }

    private async Task<AlbumDetail?> TryGetAlbumDetailAsync(string albumId)
    {
        try
        {
            var (html, _) = await GetAlbumHtmlAsync(albumId).ConfigureAwait(false);
            return JmHtmlParser.ParseAlbum(html, albumId);
        }
        catch (Exception ex)
        {
            BotLog.Info($"[JmParser] JM{albumId} 元信息获取失败，预览卡片将使用默认标题: {ex.Message}");
            return null;
        }
    }

    private static JmDownloadedAlbum CreateCdnAlbumResult(string albumId, AlbumDetail? albumDetail, string pageDir, IEnumerable<int> savedPages)
    {
        var pages = savedPages
            .Distinct()
            .Order()
            .Select(index => Path.Combine(pageDir, $"001-{index:00000}.jpg"))
            .Where(File.Exists)
            .Select(path =>
            {
                var info = ImageInfo.Read(path);
                return new PdfImagePage(path, info.Width, info.Height);
            })
            .ToArray();

        BotLog.Info($"[JmParser] JM{albumId} CDN 下载完成，共 {pages.Length} 张图片。");
        return new JmDownloadedAlbum(
            albumId,
            albumDetail?.Title ?? $"JM{albumId}",
            albumDetail?.ViewCount,
            albumDetail?.LikeCount,
            pages);
    }

    private async Task<bool> TryDownloadAndSaveImageAsync(string url, string referer, string photoId, string savePath)
    {
        try
        {
            var imageName = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
            var num = JmImageDecoder.CalculateScrambleNum(DefaultScrambleId, photoId, imageName);
            await DownloadAndSaveImageAsync(url, referer, num, savePath).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCdnImageUrl(string photoId, int index, string domain, string suffix) =>
        $"https://{domain}/media/photos/{photoId}/{index:00000}{suffix}";

    public void Dispose() => _http.Dispose();

    private static readonly HashSet<string> BlockedHosts =
    [
        "t.me", "telegram.org", "telegram.me"
    ];

    private async Task<(string Html, string Url)> GetHtmlAsync(string path)
    {
        var errors = new List<string>();
        using var cts = new CancellationTokenSource();
        var tasks = (await GetDomainsAsync().ConfigureAwait(false))
            .Select(domain => TryGetHtmlFromDomainAsync(domain, path, cts.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            var result = await finished.ConfigureAwait(false);
            if (result.Success)
            {
                cts.Cancel();
                return (result.Html, result.Url);
            }

            errors.Add(result.Error);
        }

        throw new InvalidOperationException("所有 JM 网页域名都请求失败: " + string.Join("; ", errors));
    }

    private async Task<HtmlFetchResult> TryGetHtmlFromDomainAsync(string domain, string path, CancellationToken cancellationToken)
    {
        var url = $"https://{domain.TrimEnd('/')}{path}";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(HtmlDomainTimeoutSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHtmlHeaders(req, domain);
            using var resp = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;

            var finalHost = new Uri(finalUrl).Host;
            if (BlockedHosts.Contains(finalHost))
            {
                return HtmlFetchResult.Fail($"{domain} → {finalHost}: 重定向到非 JM 站点，跳过");
            }

            var text = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return HtmlFetchResult.Fail($"{domain}: {(int)resp.StatusCode}");
            }

            if (text.Contains("Restricted Access!", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlFetchResult.Fail($"{domain}: Restricted Access");
            }

            var decoded = JmHtmlParser.DecodeBase64Html(text);
            if (IsJmErrorPage(finalUrl, decoded))
            {
                return HtmlFetchResult.Fail($"{domain}: JM 错误页 {finalUrl}");
            }

            if (!IsJmHtml(decoded))
            {
                return HtmlFetchResult.Fail($"{domain}: 响应内容不包含 JM 特征标记（scramble_id/page_arr），跳过");
            }

            return HtmlFetchResult.Ok(decoded, finalUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HtmlFetchResult.Fail($"{domain}: timeout");
        }
        catch (Exception ex)
        {
            return HtmlFetchResult.Fail($"{domain}: {ex.Message}");
        }
    }

    private static bool IsJmErrorPage(string finalUrl, string html)
    {
        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.StartsWith("/error/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return html.Contains("album_missing", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("本子不存在", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("漫畫不存在", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("漫画不存在", StringComparison.OrdinalIgnoreCase);
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

    // A downloader-wide gate also bounds simultaneous albums and CDN probes.
    internal async Task DownloadAndSaveImageAsync(string url, string referer, int num, string savePath)
    {
        await _imageGate.WaitAsync().ConfigureAwait(false);
        var temporary = savePath + "." + Guid.NewGuid().ToString("N") + ".download";
        var output = temporary + ".jpg";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            await DownloadToFileAsync(url, referer, temporary).ConfigureAwait(false);
            JmImageDecoder.DecodeAndSaveJpeg(temporary, num, output);
            File.Move(output, savePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
                File.Delete(output);
            }
            finally { _imageGate.Release(); }
        }
    }

    private async Task DownloadToFileAsync(string url, string referer, string path)
    {
        for (var attempt = 1; attempt <= MaxImageRetries; attempt++)
        {
            try
            {
                await DownloadWithSpeedCheckAsync(url, referer, path).ConfigureAwait(false);
                return;
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

    private async Task DownloadWithSpeedCheckAsync(string url, string referer, string path)
    {
        using var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var monitorCts = new CancellationTokenSource();

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        req.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false);

        var contentLength = resp.Content.Headers.ContentLength ?? -1;
        const long maxBytes = 32 * 1024 * 1024;
        if (contentLength > maxBytes) throw new InvalidDataException("图片压缩文件超过 32 MB 限制。");
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
        long downloaded = 0;

        var buffer = new byte[81920];
        var speedCheck = MonitorSpeedAsync(() => Interlocked.Read(ref downloaded), monitorCts.Token, () => downloadCts.Cancel());
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, downloadCts.Token).ConfigureAwait(false)) > 0)
            {
                if (downloaded + bytesRead > maxBytes) throw new InvalidDataException("图片压缩文件超过 32 MB 限制。");
                await file.WriteAsync(buffer.AsMemory(0, bytesRead), downloadCts.Token).ConfigureAwait(false);
                Interlocked.Add(ref downloaded, bytesRead);
            }

            monitorCts.Cancel();
            await AwaitMonitorShutdownAsync(speedCheck).ConfigureAwait(false);
            await file.FlushAsync(downloadCts.Token).ConfigureAwait(false);
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

    private static async Task MonitorSpeedAsync(Func<long> getPosition, CancellationToken ct, Action cancelDownload)
    {
        var lastPosition = 0L;
        var slowStart = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            var currentPosition = getPosition();
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

    private static void AddHtmlHeaders(HttpRequestMessage request, string domain)
    {
        var host = domain.TrimEnd('/');
        request.Headers.Host = host;
        request.Headers.Referrer = new Uri($"https://{host}/");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("DNT", "1");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Priority", "u=0, i");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA", "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", "?0");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
    }

    private async Task<IReadOnlyList<string>> GetDomainsAsync()
    {
        if (_domains is not null) return _domains;

        var domains = new List<string>();
        AddDomain(domains, "18comic.vip");
        AddDomain(domains, "jmcomic1.me");
        AddDomain(domains, "jmcomic.me");

        await TryAddRedirectDomainAsync(domains).ConfigureAwait(false);
        await TryAddPublishPageDomainsAsync(domains).ConfigureAwait(false);

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
    private static readonly Regex ViewCountPattern = new("(?:總觀看|总观看|觀看|观看|瀏覽|浏览)[^0-9]*?(?<count>[0-9][0-9,\\.]*(?:\\s*[KkMm]|\\s*[萬万])?)", RegexOptions.Compiled);
    private static readonly Regex LikeCountPattern = new("(?:喜歡|喜欢|愛心|爱心|讚|赞)[^0-9]*?(?<count>[0-9][0-9,\\.]*(?:\\s*[KkMm]|\\s*[萬万])?)", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new("data-album=\"(?<id>\\d+)\"[^>]*>[\\s\\S]*?第(?<index>\\d+)[话話](?<title>[\\s\\S]*?)<", RegexOptions.Compiled);
    private static readonly Regex PhotoIdPattern = new("<meta property=\"og:url\" content=\".*?/photo/(?<id>\\d+)/?.*?\">", RegexOptions.Compiled);
    private static readonly Regex ScramblePattern = new("var scramble_id = (?<id>\\d+);", RegexOptions.Compiled);
    private static readonly Regex PageArrayPattern = new("var page_arr = (?<json>.*?);", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageDomainPattern = new("(?:src|data-original)=\"(?:https?:)?//(?<domain>.*?)/media/", RegexOptions.Compiled);
    private static readonly Regex FirstOriginalPattern = new("data-original=\"(?<url>(?:https?:)?//[^\"]+/media/photos/[^\"]+|/media/photos/[^\"]+)\"", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex OriginalPattern = new("data-original=\"(?<url>.*?)\"", RegexOptions.Compiled);

    public static AlbumDetail ParseAlbum(string html, string fallbackId)
    {
        var albumId = MatchOrDefault(AlbumIdPattern, html, "id") ?? fallbackId;
        var scrambleId = MatchOrDefault(ScramblePattern, html, "id") ?? "220980";
        var title = CleanHtml(MatchOrDefault(AlbumTitlePattern, html, "title") ?? $"JM{albumId}");
        var viewCount = NormalizeCount(MatchOrDefault(ViewCountPattern, html, "count"));
        var likeCount = NormalizeCount(MatchOrDefault(LikeCountPattern, html, "count"));
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
        return new AlbumDetail(albumId, scrambleId, title, viewCount, likeCount, chapters);
    }

    public static PhotoDetail ParsePhoto(string html, ChapterDetail chapter, string albumScrambleId, string pageUrl)
    {
        var photoId = MatchOrDefault(PhotoIdPattern, html, "id") ?? chapter.PhotoId;
        var scrambleId = MatchOrDefault(ScramblePattern, html, "id") ?? albumScrambleId;
        var firstOriginal = WebUtility.HtmlDecode(MatchOrDefault(FirstOriginalPattern, html, "url") ?? string.Empty);
        var imageUrls = ParseImageUrlsFromPageArray(html, photoId, firstOriginal, pageUrl);

        if (imageUrls.Count == 0)
        {
            foreach (Match match in OriginalPattern.Matches(html))
            {
                var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
                url = NormalizeImageUrl(url, pageUrl);
                if (IsPhotoImageUrl(url) && imageUrls.Contains(url) == false) imageUrls.Add(url);
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

    private static List<string> ParseImageUrlsFromPageArray(string html, string photoId, string firstOriginal, string pageUrl)
    {
        var imageUrls = new List<string>();
        var arrayJson = MatchOrDefault(PageArrayPattern, html, "json");
        if (string.IsNullOrWhiteSpace(arrayJson)) return imageUrls;

        var pageArr = JsonSerializer.Deserialize<List<string>>(arrayJson) ?? [];
        var domain = MatchOrDefault(ImageDomainPattern, html, "domain");
        if (string.IsNullOrWhiteSpace(domain) && Uri.TryCreate(firstOriginal, UriKind.Absolute, out var firstUri)) domain = firstUri.Host;
        if (string.IsNullOrWhiteSpace(domain) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)) domain = pageUri.Host;
        if (string.IsNullOrWhiteSpace(domain)) return imageUrls;

        var query = ExtractQuery(firstOriginal);
        if (query.Length == 0) query = $"v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        foreach (var imgName in pageArr)
        {
            var url = $"https://{domain}/media/photos/{photoId}/{imgName}";
            if (query.Length != 0) url += "?" + query;
            imageUrls.Add(url);
        }

        return imageUrls;
    }

    private static string NormalizeImageUrl(string url, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, url, out var absoluteUri)) return absoluteUri.ToString();
        return url;
    }

    private static bool IsPhotoImageUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.AbsolutePath.Contains("/media/photos/", StringComparison.OrdinalIgnoreCase);
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

    private static string? NormalizeCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Replace(" ", string.Empty).Trim();
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

    internal const long MaxImagePixels = 16_000_000;
    private static readonly Configuration ImageConfiguration = CreateImageConfiguration();

    private static Configuration CreateImageConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        // Keep this plugin's retained pixel pool small without changing other plugins.
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            MaximumPoolSizeMegabytes = 32,
            AllocationLimitMegabytes = 128
        });
        return configuration;
    }

    public static void DecodeAndSaveJpeg(string inputPath, int num, string savePath)
    {
        var options = new DecoderOptions { Configuration = ImageConfiguration, MaxFrames = 1 };
        var info = Image.Identify(options, inputPath);
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxImagePixels)
            throw new InvalidDataException($"图片尺寸 {info.Width}×{info.Height} 超过 {MaxImagePixels} 像素限制。");

        using var src = Image.Load<Rgba32>(options, inputPath);
        src.Mutate(x => x.AutoOrient());
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        var encoder = new JpegEncoder { Quality = 92 };
        if (num == 0)
        {
            src.SaveAsJpeg(savePath, encoder);
            return;
        }

        // Preserve the old white backdrop for transparent scrambled PNG/WebP pixels.
        src.Mutate(x => x.BackgroundColor(Color.White));
        using var decoded = DecodeScrambledImage(src, num);
        decoded.SaveAsJpeg(savePath, encoder);
    }

    internal static Image<Rgba32> DecodeScrambledImage(Image<Rgba32> src, int num)
    {
        if (num <= 0) throw new ArgumentOutOfRangeException(nameof(num));
        var decoded = new Image<Rgba32>(src.Configuration, src.Width, src.Height);
        try
        {
            var over = src.Height % num;
            var stripHeight = src.Height / num;
            src.ProcessPixelRows(decoded, (source, destination) =>
            {
                for (var i = 0; i < num; i++)
                {
                    var ySrc = src.Height - stripHeight * (i + 1) - over;
                    var yDst = stripHeight * i + (i == 0 ? 0 : over);
                    var rows = stripHeight + (i == 0 ? over : 0);
                    for (var row = 0; row < rows; row++)
                        source.GetRowSpan(ySrc + row).CopyTo(destination.GetRowSpan(yDst + row));
                }
            });
            return decoded;
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
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

internal sealed record AlbumDetail(string AlbumId, string ScrambleId, string Title, string? ViewCount, string? LikeCount, List<ChapterDetail> Chapters);

internal sealed record ChapterDetail(string PhotoId, int Index, string Title);

internal sealed record PhotoDetail(string PhotoId, string ScrambleId, List<string> ImageUrls);

internal sealed record HtmlFetchResult(bool Success, string Html, string Url, string Error)
{
    public static HtmlFetchResult Ok(string html, string url) => new(true, html, url, string.Empty);

    public static HtmlFetchResult Fail(string error) => new(false, string.Empty, string.Empty, error);
}

internal sealed record JmDownloadedAlbum(string AlbumId, string Title, string? ViewCount, string? LikeCount, IReadOnlyList<PdfImagePage> Pages);

internal sealed record PdfImagePage(string Path, uint Width, uint Height);

internal sealed record PdfBuildResult(
    string PdfPath,
    string FileName,
    string? CoverPath,
    string Title,
    int PageCount,
    string? ViewCount,
    string? LikeCount)
{
    public string? PreviewUrl { get; init; }
}
