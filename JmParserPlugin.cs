using System.Reflection;
using ShiroBot.AvaloniaSdk;
using ShiroBot.JmParser.Service;
using ShiroBot.JmParser.Views;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.JmParser;

[BotPlugin(id:"JmParser",
    Description = "JM 解析插件",
    Version = "1.1.0",
    Author = "greepar",
    Category = PluginCategory.Media,
    GithubRepo = "greepar/ShiroBot.Plugin.JmParser",
    IsPluginSingleFile = false)]
public sealed class JmParserPlugin : PluginBase
{
    private const string Command = "#jm";
    private JmComicDownloader? _downloader;
    private JmPdfBuilder? _pdfBuilder;
    private Timer? _cleanupTimer;
    private IDisposable? _configWatchSubscription;
    private string _dataDir = string.Empty;
    private TimeSpan _retention = TimeSpan.FromMinutes(60);
    private OutputMode _outputMode = OutputMode.File;
    private bool _sendCover = true;
    private double _coverBlurRadius = 12;
    private string _proxy = string.Empty;
    private int _maxConcurrency = 16;

    public override string Name => "JmParser";

    protected override Task LoadAsync()
    {
        var config = Context.Config.Load<PluginConfig>();
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(pluginDir, "data", "jm");
        Directory.CreateDirectory(_dataDir);
        ApplyConfig(config);
        _configWatchSubscription = Context.Config.Watch<PluginConfig>(ApplyConfig);

        if (config.DeleteAfterMinutes > 0)
        {
            JmRetentionCleaner.Cleanup(_dataDir, _retention);
            _cleanupTimer = new Timer(_ => JmRetentionCleaner.Cleanup(_dataDir, _retention), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        }

        GroupCommands.MapPrefix(Command, HandleGroupAsync);

        var networkMode = string.IsNullOrWhiteSpace(config.Proxy) ? "直连" : $"代理: {config.Proxy}";
        BotLog.Info($"[JmParser] 已加载，使用 #jm <车号> 触发下载并转换 PDF，网络模式: {networkMode}。");
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        _configWatchSubscription?.Dispose();
        _cleanupTimer?.Dispose();
        _pdfBuilder = null;
        _downloader?.Dispose();
        Context.WebHost.UnregisterOwner(Name);

        _cleanupTimer = null;
        _configWatchSubscription = null;
        _downloader = null;
        BotLog.Info("[JmParser] 已卸载。");
        return Task.CompletedTask;
    }

    private void ApplyConfig(PluginConfig config)
    {
        var proxy = config.Proxy.Trim();
        var maxConcurrency = Math.Clamp(config.MaxConcurrency, 1, 64);

        _retention = TimeSpan.FromMinutes(config.DeleteAfterMinutes);
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

    private async Task HandleGroupAsync(GroupIncomingMessage message)
    {
        if (!CommandParser.TryParseAlbumId(message.GetPlainText(), out var albumId))
        {
            await Context.Message.ReplyAsync(message, "用法: #jm <号码>，例如 #jm 438516").ConfigureAwait(false);
            return;
        }

        BotLog.Info($"[JmParser] JM{albumId} 开始处理，群 {message.Group.GroupId}，用户 {message.SenderId}。");
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

            string? fileId = null;
            if (_outputMode is OutputMode.File or OutputMode.Both)
            {
                var upload = await Context.File.UploadGroupFileAsync(message.Group.GroupId, new Uri(result.PdfPath).AbsoluteUri, result.FileName).ConfigureAwait(false);
                fileId = upload.FileId;
            }

            await Context.Message.ReplyAsync(message, BuildSuccessMessage(albumId, fileId, result)).ConfigureAwait(false);
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
            var previewUrl = Context.WebHost.RegisterFile(Name, "pdf", result.PdfPath, expiresAfter, "application/pdf");
            result = result with { PreviewUrl = previewUrl };

            return result;
        }
    }

    private string BuildSuccessMessage(string albumId, string? fileId, PdfBuildResult result)
    {
        var message = string.IsNullOrWhiteSpace(fileId)
            ? $"JM{albumId} 已生成 PDF。"
            : $"JM{albumId} 已生成并上传为 PDF。FileId: {fileId}";
        if (string.IsNullOrWhiteSpace(result.PreviewUrl)) return message;
        var retentionText = _retention.TotalMinutes > 0 ? $"，链接约 {_retention.TotalMinutes:0} 分钟后失效" : string.Empty;
        message += $"\n临时预览: {result.PreviewUrl}{retentionText}";

        return message;
    }

    private async Task<ImageOutgoingSegment?> RenderPreviewAsync(PdfBuildResult result)
    {
        if (Context.Render is null || string.IsNullOrWhiteSpace(result.CoverPath) || !File.Exists(result.CoverPath))
        {
            return null;
        }

        var png = await Context.RenderControlPngAsync<PreviewCard>(
            new PreviewCardViewModel(result, _coverBlurRadius),
            new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);

        return new ImageOutgoingSegment("base64://" + Convert.ToBase64String(png));
    }

    private static string BuildStartingMessage(string albumId) => $"开始解析 JM{albumId}，下载和生成 PDF 可能需要一段时间。";
}
