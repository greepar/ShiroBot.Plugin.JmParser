using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShiroBot.JmParser.Service;

namespace ShiroBot.JmParser.Tests;

[TestClass]
public sealed class PreviewUrlTests
{
    [TestMethod]
    public void PublicDomainOverridesOriginAndKeepsRegisteredPath()
    {
        var registered = "http://127.0.0.1:7001/plugin/JmParser/fmdt";
        Assert.AreEqual(registered, PreviewUrlOptions.UsePublicBase(registered, ""));
        Assert.AreEqual("https://jm.example.com/plugin/JmParser/fmdt",
            PreviewUrlOptions.UsePublicBase(registered, PreviewUrlOptions.Normalize(" https://jm.example.com/ ")));
        Assert.AreEqual("http://192.168.1.10:7001", PreviewUrlOptions.Normalize("http://192.168.1.10:7001"));
    }

    [TestMethod]
    public void PublicDomainRejectsListenerWildcardsAndInvalidUrls()
    {
        foreach (var value in new[] { "http://0.0.0.0:7001", "http://[::]:7001", "ftp://jm.example.com",
            "https://user:password@jm.example.com" })
            Assert.ThrowsException<InvalidOperationException>(() => PreviewUrlOptions.Normalize(value), value);
        Assert.AreEqual("https://jm.example.com", PreviewUrlOptions.Normalize("https://jm.example.com/anything?key=x#fragment"));
        Assert.AreEqual("", PreviewUrlOptions.Normalize(" "));
    }
}
