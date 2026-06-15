namespace ShiroBot.JmParser;

public sealed class PluginConfig
{
    /// <summary>
    /// Optional HTTP proxy, for example: http://127.0.0.1:7000
    /// Leave empty to connect directly.
    /// </summary>
    public string Proxy { get; set; } = string.Empty;

    /// <summary>
    /// Output mode: file, url, or both.
    /// file uploads the PDF; url sends the temporary preview link; both does both.
    /// </summary>
    public string OutputMode { get; set; } = "file";

    /// <summary>
    /// Minutes to keep generated PDFs and downloaded images. Set 0 or less to disable cleanup.
    /// </summary>
    public int DeleteAfterMinutes { get; set; } = 60;

    /// <summary>
    /// Max concurrent downloads for images. Default 16.
    /// </summary>
    public int MaxConcurrency { get; set; } = 16;
}
