using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShiroBot.SDK.Plugin;
using ShiroBot.JmParser.Service;

namespace ShiroBot.JmParser.Tests;

[TestClass]
public sealed class PreviewFileRoutesTests
{
    [TestMethod]
    public async Task ExistingMapInterfaceServesPdfExpiresAndUnregistersEveryConfiguredName()
    {
        var file = Path.GetTempFileName();
        var now = DateTimeOffset.UtcNow;
        var host = new ExistingWebHost();
        using var routes = new PreviewFileRoutes(host, () => now);
        try
        {
            var url = routes.Register("jm", file, TimeSpan.FromMinutes(1));
            var path = new Uri(url).AbsolutePath;
            StringAssert.StartsWith(path, "/plugin/jm/");
            Assert.AreEqual(4, path.Split('/').Length);
            Assert.IsFalse(path.Contains("/pdf/"));
            var result = (PhysicalFileHttpResult)await host.Routes[path](new DefaultHttpContext());
            Assert.AreEqual("application/pdf", result.ContentType);
            Assert.IsTrue(result.EnableRangeProcessing);
            now = now.AddMinutes(2);
            Assert.IsInstanceOfType<NotFound>(await host.Routes[path](new DefaultHttpContext()));
            var next = new Uri(routes.Register("custom", file, null)).AbsolutePath;
            File.Delete(file);
            Assert.IsInstanceOfType<NotFound>(await host.Routes[next](new DefaultHttpContext()));
            routes.Dispose();
            Assert.AreEqual(0, host.Routes.Count);
            Assert.ThrowsException<ObjectDisposedException>(() => routes.Register("jm", file, null));
        }
        finally { File.Delete(file); }
    }

    private sealed class ExistingWebHost : IWebHostContext
    {
        public bool IsEnabled => true;
        public Dictionary<string, Func<HttpContext, Task<IResult>>> Routes { get; } = [];
        public string MapGet(string ownerId, string routePath, Func<HttpContext, Task<IResult>> handler)
        {
            var path = $"/plugin/{ownerId}/{routePath}";
            Routes.Add(path, handler);
            return "http://127.0.0.1:7001" + path;
        }
        public void UnregisterOwner(string ownerId)
        {
            foreach (var path in Routes.Keys.Where(path => path.StartsWith($"/plugin/{ownerId}/")).ToArray())
                Routes.Remove(path);
        }
        public string RegisterFile(string a, string b, string c, TimeSpan? d = null, string? e = null) => throw new Exception("Must not require RegisterFile changes.");
        public string Map(string a, string b, string c, Func<HttpContext, Task<IResult>> d) => throw new NotSupportedException();
        public string MapPost(string a, string b, Func<HttpContext, Task<IResult>> c) => throw new NotSupportedException();
    }
}
