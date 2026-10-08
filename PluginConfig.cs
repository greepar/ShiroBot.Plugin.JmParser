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

    [ConfigField("填写用户实际能访问的地址，例如 https://jm.qwq.lu:6；也支持 http://实际IP:端口，省略协议时默认 HTTPS。留空继承宿主公开地址。只取协议、域名和端口，忽略填写的路径、查询和片段，文件后缀由插件生成。\n此项不改变监听端口：宿主监听地址在配置中心 HTTP API 中设置；0.0.0.0 是监听通配地址，不能填作公开地址。反代转发方法见下方省略插件路径开关。", Label = "预览公开地址", Placeholder = "https://jm.qwq.lu:6")]
    public string PreviewPublicBaseUrl { get; set; } = string.Empty;

    [ConfigField("默认 JmParser，宿主内部文件地址为 /plugin/JmParser/随机标识；改成 jm 后为 /plugin/jm/随机标识。仅允许 1–64 个字母、数字、横线或下划线，请避免与其他插件重复。\n使用反代省略路径时，此名称必须与 Nginx proxy_pass 末尾路径一致，默认名称对应 /plugin/JmParser/，填写 jm 对应 /plugin/jm/。修改后请同步修改反代；旧链接需要原转发规则保留到过期。", Label = "预览路径名称")]
    public string PreviewPathName { get; set; } = "JmParser";

    [ConfigField("默认关闭，发送公开地址/plugin/预览路径名称/随机标识；开启后只发送公开地址/随机标识，必须填写预览公开地址并配置反代，宿主内部路径仍然存在。\n示例：公开地址 https://jm.qwq.lu:6、路径名称 jm、宿主监听 127.0.0.1:7001；在 jm.qwq.lu 的 Nginx server（监听端口 6，并按公开协议配置 TLS）中添加：\nlocation / { proxy_pass http://127.0.0.1:7001/plugin/jm/; }\nproxy_pass 末尾的 / 必须保留。这样公开 /fmdt 会转发到宿主 /plugin/jm/fmdt；宿主仍可从原完整路径访问。Nginx 与宿主不在同一机器时，将 127.0.0.1:7001 替换为 Nginx 能访问的宿主地址。\n关闭此开关时，应直接转发完整路径，例如 location / { proxy_pass http://127.0.0.1:7001; }，不要再次添加 /plugin/jm/。这里只调整发出的链接，不会自动设置 DNS、监听或 Nginx。", Label = "公开链接省略插件路径")]
    public bool PreviewUseRootPath { get; set; } = false;

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
