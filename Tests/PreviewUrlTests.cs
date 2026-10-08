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
    public void RootPublicLinkUsesOnlyTokenAndRequiresAnExplicitOrigin()
    {
        var registered = "http://127.0.0.1:7001/plugin/jm/fmdt";
        Assert.AreEqual("https://jm.qwq.lu:6/fmdt",
            PreviewUrlOptions.UsePublicBase(registered, PreviewUrlOptions.Normalize("jm.qwq.lu:6"), true));
        Assert.AreEqual("http://192.168.1.10:7001/fmdt",
            PreviewUrlOptions.UsePublicBase(registered, "http://192.168.1.10:7001", true));
        Assert.ThrowsException<InvalidOperationException>(() => PreviewUrlOptions.UsePublicBase(registered, "", true));
    }

    [TestMethod]
    public void PathNamesAreExplicitSafeSegments()
    {
        Assert.AreEqual("jm", PreviewUrlOptions.NormalizePathName(" jm "));
        foreach (var name in new[] { "", "../jm", "jm/pdf", "jm?x", new string('x', 65) })
            Assert.ThrowsException<InvalidOperationException>(() => PreviewUrlOptions.NormalizePathName(name));
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
