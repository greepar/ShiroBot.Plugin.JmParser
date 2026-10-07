using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShiroBot.JmParser.Service;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace ShiroBot.JmParser.Tests;

[TestClass]
public class ImagePipelineTests
{
    [TestMethod]
    public void StripsRestoreExactPixelsIncludingRemaindersAndShortImages()
    {
        foreach (var height in new[] { 1, 7, 10, 23, 40 })
        foreach (var strips in new[] { 2, 10, 16 })
        {
            using var source = new Image<Rgba32>(3, height);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < 3; x++) source[x, y] = new Rgba32((byte)y, (byte)x, 27, (byte)(100 + y));
            using var actual = JmImageDecoder.DecodeScrambledImage(source, strips);
            var rows = height / strips;
            var extra = height % strips;
            for (var i = 0; i < strips; i++)
            {
                var sourceY = height - rows * (i + 1) - extra;
                var targetY = rows * i + (i == 0 ? 0 : extra);
                for (var y = 0; y < rows + (i == 0 ? extra : 0); y++)
                    for (var x = 0; x < 3; x++) Assert.AreEqual(source[x, sourceY + y], actual[x, targetY + y]);
            }
        }
    }

    [TestMethod]
    public void UnshuffledImageSavesAndOversizedImageIsRejected()
    {
        var directory = NewDirectory();
        try
        {
            var input = Path.Combine(directory, "input.png");
            var output = Path.Combine(directory, "output.jpg");
            using (var image = new Image<Rgba32>(20, 30)) image.SaveAsPng(input);
            JmImageDecoder.DecodeAndSaveJpeg(input, 0, output);
            var info = Image.Identify(output);
            Assert.AreEqual(20, info.Width);
            Assert.AreEqual(30, info.Height);
            using (var image = new Image<Rgba32>(4001, 4000)) image.SaveAsPng(input);
            File.Delete(output);
            Assert.ThrowsException<InvalidDataException>(() => JmImageDecoder.DecodeAndSaveJpeg(input, 0, output));
            Assert.IsFalse(File.Exists(output));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SharedGateBoundsParallelDownloadsAndCleansFailedFiles()
    {
        var directory = NewDirectory();
        try
        {
            byte[] png;
            using (var image = new Image<Rgba32>(3, 7))
            using (var stream = new MemoryStream()) { image.SaveAsPng(stream); png = stream.ToArray(); }
            var handler = new TestHandler(png);
            using var downloader = new JmComicDownloader(new HttpClient(handler), 2);
            await Task.WhenAll(Enumerable.Range(0, 32).Select(i => downloader.DownloadAndSaveImageAsync("https://example.test/image", "https://example.test/", 0, Path.Combine(directory, i + ".jpg"))));
            Assert.IsTrue(handler.Peak <= 2);
            Assert.AreEqual(32, Directory.GetFiles(directory).Length);
            handler.Invalid = true;
            await Assert.ThrowsExceptionAsync<UnknownImageFormatException>(() => downloader.DownloadAndSaveImageAsync("https://example.test/image", "https://example.test/", 0, Path.Combine(directory, "failed.jpg")));
            Assert.AreEqual(32, Directory.GetFiles(directory).Length);
            handler.Invalid = false;
            await downloader.DownloadAndSaveImageAsync("https://example.test/image", "https://example.test/", 0, Path.Combine(directory, "recovered.jpg"));
            Assert.AreEqual(33, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, true); }
    }


    [TestMethod]
    public void OrientationMetadataAndAnimatedInputsProduceOneCorrectPage()
    {
        var directory = NewDirectory();
        try
        {
            var input = Path.Combine(directory, "input.jpg");
            var output = Path.Combine(directory, "output.jpg");
            using (var image = new Image<Rgba32>(20, 30))
            {
                image.Metadata.ExifProfile = new ExifProfile();
                image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
                image.SaveAsJpeg(input);
            }
            JmImageDecoder.DecodeAndSaveJpeg(input, 0, output);
            Assert.AreEqual(30, Image.Identify(output).Width);
            Assert.AreEqual(20, Image.Identify(output).Height);
            input = Path.Combine(directory, "animated.gif");
            using (var image = new Image<Rgba32>(20, 30, Color.Red))
            using (var second = new Image<Rgba32>(20, 30, Color.Blue))
            {
                image.Frames.AddFrame(second.Frames.RootFrame);
                image.SaveAsGif(input);
            }
            JmImageDecoder.DecodeAndSaveJpeg(input, 0, output);
            using var result = Image.Load<Rgba32>(output);
            Assert.AreEqual(1, result.Frames.Count);
            Assert.IsTrue(result[10, 15].R > 240);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task OversizedResponsesAreRejectedWithOrWithoutContentLength()
    {
        var directory = NewDirectory();
        try
        {
            foreach (var declaredLength in new[] { true, false })
            {
                using var downloader = new JmComicDownloader(new HttpClient(new OversizedHandler(declaredLength)), 2);
                await Assert.ThrowsExceptionAsync<InvalidDataException>(() => downloader.DownloadAndSaveImageAsync("https://example.test/image", "https://example.test/", 0, Path.Combine(directory, "failed.jpg")));
                Assert.AreEqual(0, Directory.GetFiles(directory).Length);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class OversizedHandler(bool declaredLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = declaredLength ? new ByteArrayContent(new byte[] { 1 }) : new StreamContent(new GeneratedStream());
            if (declaredLength) content.Headers.ContentLength = 32 * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class GeneratedStream : Stream
    {
        private long remaining = 32 * 1024 * 1024 + 1;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            buffer[..count].Clear();
            remaining -= count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "jm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestHandler(byte[] png) : HttpMessageHandler
    {
        private int active;
        public int Peak;
        public bool Invalid;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref active);
            Peak = Math.Max(Peak, count);
            try
            {
                await Task.Delay(15, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Invalid ? new byte[] { 1, 2, 3 } : png) };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
