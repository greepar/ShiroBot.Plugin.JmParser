using Microsoft.AspNetCore.Http;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.JmParser.Service;

/// <summary>Uses existing custom routes so preview URLs need no extra file-type prefix.</summary>
internal sealed class PreviewFileRoutes(IWebHostContext webHost, Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string Register(string owner, string filePath, TimeSpan? expiresAfter)
    {
        var fullPath = Path.GetFullPath(filePath);
        var now = clock ?? (() => DateTimeOffset.UtcNow);
        var expiresAt = expiresAfter is { TotalSeconds: > 0 }
            ? now().Add(expiresAfter.Value) : DateTimeOffset.MaxValue;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string token;
            do { token = Guid.NewGuid().ToString("N")[..8]; }
            while (!_paths.Add(owner + "/" + token));
            _owners.Add(owner);
            return webHost.MapGet(owner, token, _ => Task.FromResult<IResult>(
                now() >= expiresAt || !File.Exists(fullPath)
                    ? Results.NotFound()
                    : Results.File(fullPath, "application/pdf", enableRangeProcessing: true)));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var owner in _owners) webHost.UnregisterOwner(owner);
            _owners.Clear();
            _paths.Clear();
        }
    }
}
