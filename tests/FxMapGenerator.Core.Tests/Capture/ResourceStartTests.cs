using FxMapGenerator.Core.Capture;

namespace FxMapGenerator.Core.Tests.Capture;

public sealed class ResourceStartTests
{
    static ResourceStart Quick(FakeGame game) => new(game)
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        Settle = TimeSpan.FromMilliseconds(50),
        HelloTimeout = TimeSpan.FromMilliseconds(400),
    };

    [Fact]
    public async Task ARunningResourceIsLeftAsItIs()
    {
        using var game = new FakeGame();
        var r = await Quick(game).RunAsync("127.0.0.1", 29200);
        Assert.Equal("running", r.State);
        Assert.Equal((FakeGame.CaptureName, "0.1.0"), (r.Hello!.Resource, r.Hello.Version));
        Assert.DoesNotContain(game.Received, c => c.StartsWith("ensure", StringComparison.Ordinal) || c == "refresh");
    }

    [Fact]
    public async Task AResourceOnTheServerIsStartedWithRefreshAndEnsure()
    {
        using var game = new FakeGame { CaptureRunning = false };
        var r = await Quick(game).RunAsync("127.0.0.1", 29200);
        Assert.Equal("started", r.State);
        Assert.True(game.CaptureRunning);
        var sent = game.Received.ToList();
        Assert.True(sent.IndexOf("refresh") < sent.IndexOf("ensure " + FakeGame.CaptureName));
        Assert.Contains(r.Console, l => l.Contains("No such command fxmapgen."));       // the first HELLO had nobody to answer it
    }

    [Fact]
    public async Task WhenItDoesNotStartTheConsolesWordsSayWhy()
    {
        using var missing = new FakeGame { CaptureRunning = false, CaptureOnServer = false };
        var r = await Quick(missing).RunAsync("127.0.0.1", 29200);
        Assert.Equal(("notStarted", "notFound"), (r.State, r.Reason));
        Assert.Null(r.Hello);
        Assert.Contains(r.Console, l => l.EndsWith($"Couldn't find resource {FakeGame.CaptureName}."));   // without FiveM's colour codes

        using var denied = new FakeGame { CaptureRunning = false, Ace = false };
        r = await Quick(denied).RunAsync("127.0.0.1", 29200);
        Assert.Equal(("notStarted", "denied"), (r.State, r.Reason));
        Assert.Contains(r.Console, l => l.Contains("Access denied for command ensure."));
        Assert.False(denied.CaptureRunning);

        // the server says nothing and nothing starts: most likely no permission to send server commands
        using var silent = new FakeGame { CaptureRunning = false, EnsureSilent = true };
        r = await Quick(silent).RunAsync("127.0.0.1", 29200);
        Assert.Equal(("notStarted", "noAnswer"), (r.State, r.Reason));
    }

    [Fact]
    public void TheReasonComesFromTheServersWords()
    {
        Assert.Equal("notFound", ResourceStart.ReasonOf(["   4.100 Couldn't find resource fxmapgen-capture."]));
        Assert.Equal("denied", ResourceStart.ReasonOf(["   4.100 Access denied for command ensure."]));
        Assert.Equal("noAnswer", ResourceStart.ReasonOf(["   1.000 > refresh", "   4.000 > ensure fxmapgen-capture"]));
    }

    [Fact]
    public async Task WithoutTheGameThereIsNothingToStart()
    {
        using var game = new FakeGame { Connectable = false };
        var r = await Quick(game).RunAsync("127.0.0.1", 29200);
        Assert.Equal("noConsole", r.State);
        Assert.Empty(game.Received);
    }
}
