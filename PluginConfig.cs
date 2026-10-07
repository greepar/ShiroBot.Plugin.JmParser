using ShiroBot.SDK.Config;

namespace ShiroBot.JmParser;

public sealed class PluginConfig
{
    /// <summary>
    /// Optional HTTP proxy, for example: http://127.0.0.1:7000
    /// Leave empty to connect directly.
    /// </summary>
    [ConfigField("留空表示直连；需要代理时填写完整 HTTP 代理地址。", Label = "代理地址", Type = "string", Placeholder = "http://127.0.0.1:7890")]
    public string Proxy { get; set; } = string.Empty;

    /// <summary>
    /// Output mode: file, url, or both.
    /// file uploads the PDF; url sends the temporary preview link; both does both.
    /// </summary>
    [ConfigField("file 上传 PDF，url 发送临时预览链接，both 两者都发送。", Label = "输出模式", Type = "select", Options = ["file", "url", "both"])]
    public string OutputMode { get; set; } = "file";

    /// <summary>
    /// Minutes to keep generated PDFs and downloaded images. Set 0 or less to disable cleanup.
    /// </summary>
    [ConfigField("生成的 PDF 和图片保留时间，0 表示不自动清理。", Label = "文件保留分钟", Type = "number", Min = 0)]
    public int DeleteAfterMinutes { get; set; } = 60;

    /// <summary>
    /// Max concurrent downloads for images. Default 2.
    /// </summary>
    [ConfigField("图片下载和解码共用的最大并发数，建议 2–4；大图和高并发会显著增加内存峰值。", Label = "最大并发下载", Type = "number", Min = 1, Max = 64)]
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>
    /// Whether to send the rendered cover preview image.
    /// </summary>
    [ConfigField("是否发送渲染后的封面预览图。", Label = "发送封面预览", Type = "boolean")]
    public bool SendCover { get; set; } = true;

    /// <summary>
    /// Blur radius used by the rendered cover preview image.
    /// </summary>
    [ConfigField("封面背景模糊半径，数值越大越模糊。", Label = "封面模糊半径", Type = "number", Min = 0, Max = 100)]
    public double CoverBlurRadius { get; set; } = 12;
}
