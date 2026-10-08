namespace ShiroBot.JmParser.Service;

internal static class PreviewUrlOptions
{
    public static string Normalize(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) return string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host is "0.0.0.0" or "::" or "[::]" or "*" ||
            uri.UserInfo.Length != 0)
            throw new InvalidOperationException("预览公开地址必须是 HTTP/HTTPS 域名或实际 IP（可带端口），不能包含账号或使用监听通配地址；完整 URL 的路径、查询和片段会忽略。监听地址请在宿主 HTTP API 中配置。");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string UsePublicBase(string registeredUrl, string publicBaseUrl)
    {
        var registered = new Uri(registeredUrl, UriKind.Absolute);
        return publicBaseUrl.Length == 0 ? registeredUrl :
            publicBaseUrl + registered.AbsolutePath;
    }
}
