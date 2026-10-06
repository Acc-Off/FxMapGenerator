using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

/// <summary>
/// Writes a sample of a visit and of pre-checks on the fake game for people to read: what the program says to the game
/// and what it hears (<c>console.log</c>), the run's log, and the test shots. Runs only when <c>FXMAPGEN_SAMPLE</c> names a
/// folder (it is emptied first).
/// </summary>
public sealed class CaptureSample
{
    static readonly VisitStage.Options Fast = new()
    {
        ResourceVersion = "0.1.0",
        ConnectTimeout = TimeSpan.FromSeconds(1),
        TileTimeout = TimeSpan.FromMilliseconds(400),
        HmapTimeout = TimeSpan.FromSeconds(3),
        SettleBeforeShot = TimeSpan.Zero,
        ShotRetryWait = TimeSpan.FromMilliseconds(1),
        NotificationWait = TimeSpan.FromSeconds(1),
        RetryWait = TimeSpan.FromMilliseconds(1),
        StopAtEnd = false,                                                 // the tests of stopping the resource set it
    };

    [SkippableFact]
    public async Task WritesASampleVisitAndPrechecks()
    {
        var output = Environment.GetEnvironmentVariable("FXMAPGEN_SAMPLE");
        Skip.If(string.IsNullOrEmpty(output), "FXMAPGEN_SAMPLE is not set");
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
        using var tmp = new TempFolder();

        // a visit of six blocks: a black frame, the frame of the block before and a notification on one block, a READY that
        // comes late on another, a scene that does not settle once on a third; then the run continues from where it was
        var blocks = new[] { "z8_60_128", "z8_64_128", "z8_68_128", "z8_60_132", "z8_64_132", "z8_68_132" }.Select(BlockId.Parse).ToList();
        using (var game = new FakeGame())
        {
            game.Fault("z8_64_128", ShotFault.Black, ShotFault.Stale, ShotFault.Notification);
            game.Fault("z8_68_132", TileFault.LateReady);
            game.Fault("z8_60_132", TileFault.Unsettled);
            var project = NewProject(tmp, "visit", blocks);
            var end = await Run(project, game);
            Copy(end.RunFolder, output, "visit-");
            File.WriteAllText(Path.Combine(output, "visit-commands.txt"), string.Join('\n', game.Received) + "\n");
        }

        // another player comes on during the visit: the server refuses to clear the world, the run stops with the reason
        using (var game = new FakeGame())
        {
            game.Fault("z8_68_128", TileFault.Refused);
            var end = await Run(NewProject(tmp, "refused", blocks), game);
            Copy(end.RunFolder, output, "refused-");
            File.WriteAllText(Path.Combine(output, "refused-snapshot.txt"), $"state {end.State}\nstopReason {end.StopReason}\n" +
                string.Join('\n', end.Failures.Select(f => $"failed {f.Unit}: {f.Message}")) + "\n");
        }

        // pre-checks: all fine, and with something left drawing on the screen (a HUD) and the weather not fixed
        var options = new Precheck.Options { ResourceVersion = "0.1.0", SettleBeforeShot = TimeSpan.Zero, ShotRetryWait = TimeSpan.FromMilliseconds(1) };
        using (var game = new FakeGame())
        {
            var report = await new Precheck(game, options).RunAsync(NewProject(tmp, "fine", blocks));
            Copy(report.Folder, output, "precheck-fine-");
        }
        using (var game = new FakeGame { Hud = (1500, 940, 330, 90), Weather = "CLOUDS" })
        {
            var report = await new Precheck(game, options).RunAsync(NewProject(tmp, "hud", blocks));
            Copy(report.Folder, output, "precheck-hud-");
        }
        // something at the side of the frame, where FiveM draws its own watermark: shown, but off the map
        using (var game = new FakeGame { Hud = (1760, 4, 160, 28) })
        {
            var report = await new Precheck(game, options).RunAsync(NewProject(tmp, "edge", blocks));
            Copy(report.Folder, output, "precheck-edge-");
        }
        // the settle wait: the town stays coarse for 0.5 s after the streaming ended, a few pixels keep flickering; then a
        // town whose picture never stops changing (the measurement fails, the visit keeps the default)
        using (var game = new FakeGame { HonourQuiet = true, CoarseMs = 500, Speckle = true })
        {
            var report = await new Precheck(game, options).RunAsync(NewProject(tmp, "settle", blocks));
            Copy(report.Folder, output, "precheck-settle-");
        }
        using (var game = new FakeGame { HonourQuiet = true })
        {
            foreach (var b in SettleProbe.Districts[0].Blocks) game.NeverStill.Add(b.Name);
            var report = await new Precheck(game, options).RunAsync(NewProject(tmp, "unsettled", blocks));
            Copy(report.Folder, output, "precheck-unsettled-");
        }
    }

    static Project NewProject(TempFolder tmp, string name, IEnumerable<BlockId> blocks)
    {
        var p = Project.Create(Path.Combine(tmp.File(name), "p.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Range.Add = blocks.Select(b => b.Name).ToList();
        p.Save();
        return p;
    }

    static async Task<JobSnapshot> Run(Project project, FakeGame game) => await JobRunner.Create(new JobSetup
    {
        ProjectPath = project.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(),
        Stages = (_, _) => new BuildPlan(new Stage[] { new VisitStage(game, Fast) }, Array.Empty<string>()),
    }).RunAsync();

    static void Copy(string folder, string output, string prefix)
    {
        foreach (var f in Directory.GetFiles(folder))
            File.Copy(f, Path.Combine(output, prefix + Path.GetFileName(f)), overwrite: true);
    }
}
