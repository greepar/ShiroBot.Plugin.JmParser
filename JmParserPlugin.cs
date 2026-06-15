using System.Reflection;
using ShiroBot.JmParser.Service;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.JmParser;

[BotPlugin(id:"JmParser",
    Description = "JM PDF下载插件",
    Version = "1.0.0",
    GithubRepo = "greepar/ShiroBot.Plugin.JmParser",
    IsPluginSingleFile = false)]
public sealed class JmParserPlugin : PluginBase
{
    private const string Command = "#jm";
    private JmComicDownloader? _downloader;
    private JmPdfBuilder? _pdfBuilder;
    private Timer? _cleanupTimer;
    private string _dataDir = string.Empty;
    private TimeSpan _retention = TimeSpan.FromMinutes(60);
    private OutputMode _outputMode = OutputMode.File;

    public override string Name => "JmParser";

    protected override Task LoadAsync()
    {
        var config = Context.Config.Load<PluginConfig>();
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(pluginDir, "data", "jm");
        Directory.CreateDirectory(_dataDir);
        _retention = TimeSpan.FromMinutes(config.DeleteAfterMinutes);
        _outputMode = OutputModeParser.Parse(config.OutputMode);
        _downloader = new JmComicDownloader(config.Proxy, config.MaxConcurrency);
        _pdfBuilder = new JmPdfBuilder(_downloader, _dataDir);

        if (config.DeleteAfterMinutes > 0)
        {
            JmRetentionCleaner.Cleanup(_dataDir, _retention);
            _cleanupTimer = new Timer(_ => JmRetentionCleaner.Cleanup(_dataDir, _retention), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        }

        FriendCommands.MapPrefix(Command, HandleFriendAsync);
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
        Context.WebHost.UnregisterOwner(Name);

        _cleanupTimer = null;
        _downloader = null;
        BotLog.Info("[JmParser] 已卸载。");
        return Task.CompletedTask;
    }

    private async Task HandleFriendAsync(FriendIncomingMessage message)
    {
        if (!CommandParser.TryParseAlbumId(message.GetPlainText(), out var albumId))
        {
            await Context.Message.ReplyAsync(message, "用法: #jm <车号>，例如 #jm 438516").ConfigureAwait(false);
            return;
        }

        var pdfBuilder = _pdfBuilder ?? throw new InvalidOperationException("JM PDF 生成器尚未初始化。");
        await Context.Message.ReplyAsync(message, await BuildStartingMessageAsync(pdfBuilder, albumId).ConfigureAwait(false)).ConfigureAwait(false);
        try
        {
            var result = await BuildPdfAsync(albumId).ConfigureAwait(false);
            string? fileId = null;
            if (_outputMode is OutputMode.File or OutputMode.Both)
            {
                var upload = await Context.File.UploadPrivateFileAsync(message.SenderId, new Uri(result.PdfPath).AbsoluteUri, result.FileName).ConfigureAwait(false);
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

    private async Task HandleGroupAsync(GroupIncomingMessage message)
    {
        if (!CommandParser.TryParseAlbumId(message.GetPlainText(), out var albumId))
        {
            await Context.Message.ReplyAsync(message, "用法: #jm <号码>，例如 #jm 438516").ConfigureAwait(false);
            return;
        }

        var pdfBuilder = _pdfBuilder ?? throw new InvalidOperationException("JM PDF 生成器尚未初始化。");
        await Context.Message.ReplyAsync(message, await BuildStartingMessageAsync(pdfBuilder, albumId).ConfigureAwait(false)).ConfigureAwait(false);
        try
        {
            var result = await BuildPdfAsync(albumId).ConfigureAwait(false);
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
            if (_outputMode is OutputMode.Url or OutputMode.Both)
            {
                if (!Context.WebHost.IsEnabled)
                {
                    throw new InvalidOperationException("当前输出模式需要预览 URL，但宿主 Web 服务未开启。");
                }

                var expiresAfter = _retention.TotalMinutes > 0 ? _retention : (TimeSpan?)null;
                var previewUrl = Context.WebHost.RegisterFile(Name, "pdf", result.PdfPath, expiresAfter, "application/pdf");
                result = result with { PreviewUrl = previewUrl };
            }

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

    private static async Task<string> BuildStartingMessageAsync(JmPdfBuilder pdfBuilder, string albumId)
    {
        try
        {
            var title = await pdfBuilder.FetchAlbumTitleAsync(albumId).ConfigureAwait(false);
            return $"开始解析 JM{albumId}: {title}，下载和生成 PDF 可能需要一段时间。";
        }
        catch
        {
            return $"开始解析 JM{albumId}，下载和生成 PDF 可能需要一段时间。";
        }
    }
}
