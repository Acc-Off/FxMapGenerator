using FxMapGenerator.App.Services;

namespace FxMapGenerator.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void SecondAcquireForTheSameDataDirectoryIsNotFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var first = SingleInstance.Acquire(dir);
            Assert.True(first.IsFirst);
            using (var second = SingleInstance.Acquire(dir))
            {
                Assert.False(second.IsFirst);
            }
            // A different data directory is a different instance.
            using var other = SingleInstance.Acquire(Path.Combine(dir, "other"));
            Assert.True(other.IsFirst);

            SingleInstance.Publish(dir, "http://127.0.0.1:20400/");
            Assert.Equal("http://127.0.0.1:20400/", SingleInstance.ReadUrl(dir));
            File.WriteAllText(SingleInstance.InstancePath(dir), "{\"url\":\"http://evil.example/\",\"pid\":1}");
            Assert.Null(SingleInstance.ReadUrl(dir)); // only loopback URLs are trusted
            SingleInstance.Remove(dir);
            Assert.Null(SingleInstance.ReadUrl(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
