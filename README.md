# JmParser

当前发布：`v1.3.1`。本版本使用 SDK `0.9.8`，需要宿主 `0.9.8` 的新 ABI；旧宿主不兼容。

JM 漫画解析插件，通过 `#jm <号码>` 下载漫画并生成 PDF，可选上传到 QQ 群文件或注册临时预览链接。

运行要求：宿主最低版本 0.9.8，ShiroBot API 0.9.2；使用 ShiroBot.SDK 0.9.8。QQ 群文件上传通过 `IQFileApi` 能力探测。

配置通过宿主统一接口应用；保存后等待应用完成。修改 config.toml 也由宿主监听并应用，无需重新加载插件。

## PDF 预览地址

选择 `output_mode = "url"` 或 `"both"`，可设置 `preview_public_base_url = "https://jm.example.com"`，留空继承宿主公开地址。域名或实际 IP 可带端口；若填写完整 URL，只取协议、域名和端口，路径后缀自动生成。链接格式为 `https://jm.example.com/plugin/JmParser/随机标识`，保留原有过期、文件清理和卸载失效行为。

监听仍在宿主 `[api].listen_urls` 配置，例如 `["http://0.0.0.0:7001"]`。`0.0.0.0` 表示监听所有网卡，不能作为发给用户的公开地址；域名需代理到此宿主。去掉 pdf 路径需要宿主支持空文件路由前缀的新构建。
