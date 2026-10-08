using ShiroBot.SDK.Config;
using System.Reflection;
using ShiroBot.AvaloniaSdk;
using ShiroBot.JmParser.Service;
using ShiroBot.JmParser.Views;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;

using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("0.9.2", "0.9.2")]

namespace ShiroBot.JmParser;

[BotPlugin(id:"JmParser",
    Description = "JM 解析插件",
    Version = "1.3.1",
    Author = "greepar",
    Category = PluginCategory.Media,
    GithubRepo = "greepar/ShiroBot.Plugin.JmParser",
    IsPluginSingleFile = false)]
public sealed class JmParserPlugin : PluginBase<PluginConfig>
{
    private const string Command = "#jm";
    private JmComicDownloader? _downloader;
    private JmPdfBuilder? _pdfBuilder;
    private Timer? _cleanupTimer;
    private string _dataDir = string.Empty;
    private TimeSpan _retention = TimeSpan.FromMinutes(60);
    private OutputMode _outputMode = OutputMode.File;
    private bool _sendCover = true;
    private double _coverBlurRadius = 12;
    private string _proxy = string.Empty;
    private string _previewPublicBaseUrl = string.Empty;
    private string _previewPathName = "JmParser";
    private bool _previewUseRootPath;
    private readonly HashSet<string> _previewOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _previewOwnersLock = new();
    private int _maxConcurrency = 2;

    public override string Name => "JmParser";

    protected override Task OnConfigChangedAsync(PluginConfig previous, PluginConfig current, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyConfig(current);
        return Task.CompletedTask;
    }

    protected override Task LoadAsync()
    {
        var config = Settings;
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(pluginDir, "data", "jm");
        Directory.CreateDirectory(_dataDir);
        ApplyConfig(config);

        GroupCommands.MapPrefix(Command, HandleGroupAsync);

        var networkMode = string.IsNullOrWhiteSpace(config.Proxy) ? "直连" : $"代理: {config.Proxy}";
        BotLog.Info($"[JmParser] 已加载，使用 #jm <车号> 触发下载并转换 PDF，网络模式: {networkMode}。");
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        _cleanupTimer?.Dispose();
        _pdfBuilder = null;
        _downloader?.Dispose();
        lock (_previewOwnersLock)
        {
            foreach (var owner in _previewOwners) Context.WebHost.UnregisterOwner(owner);
            _previewOwners.Clear();
        }
        Context.WebHost.UnregisterOwner(Name);

        _cleanupTimer = null;
        _downloader = null;
        BotLog.Info("[JmParser] 已卸载。");
        return Task.CompletedTask;
    }

    private void ApplyConfig(PluginConfig config)
    {
        var publicBaseUrl = PreviewUrlOptions.Normalize(config.PreviewPublicBaseUrl);
        var previewPathName = PreviewUrlOptions.NormalizePathName(config.PreviewPathName);
        if (config.PreviewUseRootPath && publicBaseUrl.Length == 0)
            throw new InvalidOperationException("省略插件路径时必须填写预览公开地址，并配置反向代理路径转发。");
        var proxy = config.Proxy.Trim();
        var maxConcurrency = Math.Clamp(config.MaxConcurrency, 1, 64);

        _previewPublicBaseUrl = publicBaseUrl;
        _previewPathName = previewPathName;
        _previewUseRootPath = config.PreviewUseRootPath;
        _retention = TimeSpan.FromMinutes(config.DeleteAfterMinutes);
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        if (config.DeleteAfterMinutes > 0)
        {
            JmRetentionCleaner.Cleanup(_dataDir, _retention);
            _cleanupTimer = new Timer(_ => JmRetentionCleaner.Cleanup(_dataDir, _retention), null,
                TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        }

        _outputMode = OutputModeParser.Parse(config.OutputMode);
        _sendCover = config.SendCover;
        _coverBlurRadius = Math.Max(0, config.CoverBlurRadius);
        if (_downloader is null ||
            !string.Equals(_proxy, proxy, StringComparison.Ordinal) ||
            _maxConcurrency != maxConcurrency)
        {
            _downloader?.Dispose();
            _proxy = proxy;
            _maxConcurrency = maxConcurrency;
            _downloader = new JmComicDownloader(_proxy, _maxConcurrency);
            _pdfBuilder = new JmPdfBuilder(_downloader, _dataDir);
        }

        BotLog.Info($"[JmParser] 配置已应用: output_mode={config.OutputMode}, send_cover={_sendCover}, cover_blur_radius={_coverBlurRadius:0.##}。");
    }

    private async Task HandleGroupAsync(MessageEvent message)
    {
        if (!CommandParser.TryParseAlbumId(message.GetPlainText(), out var albumId))
        {
            await Context.Message.ReplyAsync(message, "用法: #jm <号码>，例如 #jm 438516").ConfigureAwait(false);
            return;
        }

        BotLog.Info($"[JmParser] JM{albumId} 开始处理，群 {message.Channel.Id}，用户 {message.Sender.Id}。");
        await Context.Message.ReplyAsync(message, BuildStartingMessage(albumId)).ConfigureAwait(false);
        try
        {
            var result = await BuildPdfAsync(albumId).ConfigureAwait(false);
            if (_sendCover)
            {
                var preview = await RenderPreviewAsync(result).ConfigureAwait(false);
                if (preview is not null)
                {
                    await Context.Message.ReplyAsync(message, preview).ConfigureAwait(false);
                }
            }

            var fileSent = false;
            if (_outputMode is OutputMode.File or OutputMode.Both)
            {
                var sent = await Context.Message.ReplyAsync(message,
                    new FileSegment(new Uri(result.PdfPath).AbsoluteUri) { FileName = result.FileName }).ConfigureAwait(false);
                if (!sent.IsSuccess)
                    throw new InvalidOperationException(sent.ErrorMessage ?? "PDF 文件发送失败。");
                fileSent = true;
            }

            await Context.Message.ReplyAsync(message, BuildSuccessMessage(albumId, fileSent, result)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Error($"[JmParser] JM{albumId} 处理失败: {ex}");
            await Context.Message.ReplyAsync(message, $"JM{albumId} 处理失败: {CommandParser.TrimError(ex.Message)}").ConfigureAwait(false);
        }
    }

    private Task<PdfBuildResult> BuildPdfAsync(string albumId)
    {
        var pdfBuilder = _pdfBuilder ?? throw new InvalidOperationException("JM PDF 生成器尚未初始化。");
        return BuildAsync();

        async Task<PdfBuildResult> BuildAsync()
        {
            var result = await pdfBuilder.BuildAsync(albumId).ConfigureAwait(false);
            if (_outputMode is not (OutputMode.Url or OutputMode.Both)) return result;
            if (!Context.WebHost.IsEnabled)
            {
                throw new InvalidOperationException("当前输出模式需要预览 URL，但宿主 Web 服务未开启。");
            }

            var expiresAfter = _retention.TotalMinutes > 0 ? _retention : (TimeSpan?)null;
            var owner = _previewPathName;
            lock (_previewOwnersLock) _previewOwners.Add(owner);
            var previewUrl = Context.WebHost.RegisterFile(owner, string.Empty, result.PdfPath, expiresAfter, "application/pdf");
            previewUrl = PreviewUrlOptions.UsePublicBase(previewUrl, _previewPublicBaseUrl, _previewUseRootPath);
            result = result with { PreviewUrl = previewUrl };

            return result;
        }
    }

    private string BuildSuccessMessage(string albumId, bool fileSent, PdfBuildResult result)
    {
        var message = fileSent
            ? $"JM{albumId} 已生成并发送 PDF。"
            : $"JM{albumId} 已生成 PDF。";
        if (string.IsNullOrWhiteSpace(result.PreviewUrl)) return message;
        var retentionText = _retention.TotalMinutes > 0 ? $"，链接约 {_retention.TotalMinutes:0} 分钟后失效" : string.Empty;
        message += $"\n临时预览: {result.PreviewUrl}{retentionText}";

        return message;
    }

    private async Task<ImageSegment?> RenderPreviewAsync(PdfBuildResult result)
    {
        if (Context.Render is null || string.IsNullOrWhiteSpace(result.CoverPath) || !File.Exists(result.CoverPath))
        {
            return null;
        }

        var png = await Context.RenderControlPngAsync<PreviewCard>(
            new PreviewCardViewModel(result, _coverBlurRadius),
            new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);

        return new ImageSegment("base64://" + Convert.ToBase64String(png));
    }

    private static string BuildStartingMessage(string albumId) => $"开始解析 JM{albumId}，下载和生成 PDF 可能需要一段时间。";
}
