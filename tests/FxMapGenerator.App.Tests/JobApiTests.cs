using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.App.Cli;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace FxMapGenerator.App.Tests;

public sealed class JobApiTests
{
    /// <summary>Units that wait until the test lets them go.</summary>
    sealed class WaitingStage(int units, ManualResetEventSlim release) : Stage
    {
        public override string Id => "wait";
        public override string Row => "ortho";
        public override IReadOnlyList<string> Prepare(StageContext ctx) => Enumerable.Range(0, units).Select(i => $"u{i}").ToList();
        public override void Run(UnitContext ctx) => release.Wait(ctx.Token);
        public override int CountReady(Project project, StateStore state) => units;
    }

    sealed class FakeBuild(Func<Stage[]> stages) : IBuildStages
    {
        public BuildPlan For(Project project, StateStore state, BuildStages.Options options) => new(stages(), new[] { "a note" });
    }

    static string NewProject(string folder)
    {
        var p = Project.Create(Path.Combine(folder, "api.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.Save();
        return p.FilePath;
    }

    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task StartFollowChangeTheLevelAndStop()
    {
        using var release = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s =>
            s.AddSingleton<IBuildStages>(new FakeBuild(() => new Stage[] { new WaitingStage(500, release) })));
        var project = NewProject(host.DataDirectory);
        var jobs = host.App.Services.GetRequiredService<JobManager>();

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.GetAsync("/api/jobs/current")).StatusCode);

        var start = await host.Client.PostAsJsonAsync("/api/jobs", new { project, workers = 2 });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = await Json(start);
        Assert.Equal("running", started.GetProperty("state").GetString());
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), started.GetProperty("workers").GetInt32());
        Assert.Equal("a note", started.GetProperty("notes")[0].GetString());

        var twice = await host.Client.PostAsJsonAsync("/api/jobs", new { project });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("RUNNING", (await Json(twice)).GetProperty("error").GetProperty("code").GetString());

        // the SSE stream starts with the run
        using (var events = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead))
        {
            using var reader = new StreamReader(await events.Content.ReadAsStreamAsync());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? line;
            bool sawJob = false;
            while (!sawJob && (line = await reader.ReadLineAsync(timeout.Token)) is not null) sawJob = line == "event: job";
            Assert.True(sawJob);
        }

        var level = await host.Client.PutAsJsonAsync("/api/jobs/current/workers", new { workers = 3 });
        Assert.Equal(HttpStatusCode.OK, level.StatusCode);
        Assert.Equal(Math.Min(3, Environment.ProcessorCount), (await Json(level)).GetProperty("workers").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync("/api/jobs/current/workers", new { workers = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/jobs/current/stop", new { mode = "later" })).StatusCode);

        var stop = await host.Client.PostAsJsonAsync("/api/jobs/current/stop", new { mode = "boundary" });
        Assert.Equal("stopping", (await Json(stop)).GetProperty("state").GetString());
        release.Set();
        await jobs.WaitAsync();
        var last = await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current");
        Assert.Equal("stopped", last.GetProperty("state").GetString());
        Assert.True(Directory.Exists(last.GetProperty("runFolder").GetString()));

        var none = await host.Client.PostAsJsonAsync("/api/jobs/current/stop", new { mode = "now" });
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Equal("NO_JOB", (await Json(none)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ClosingTheAppStopsTheRunAtOnce()
    {
        using var release = new ManualResetEventSlim();
        var host = await TestHost.StartAsync(o => o.ConfigureServices = s =>
            s.AddSingleton<IBuildStages>(new FakeBuild(() => new Stage[] { new WaitingStage(5, release) })));
        var project = NewProject(host.DataDirectory);
        var jobs = host.App.Services.GetRequiredService<JobManager>();
        jobs.Start(project, null, new BuildStages.Options());
        await host.DisposeAsync();
        Assert.False(jobs.IsRunning);
        Assert.Equal(JobState.Stopped, jobs.Current!.State);
    }

    [Fact]
    public async Task BadStartRequests()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/jobs", new { workers = 2 })).StatusCode);
        var missing = await host.Client.PostAsJsonAsync("/api/jobs", new { project = Path.Combine(host.DataDirectory, "none.fxmapgen.json") });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("NOT_FOUND", (await Json(missing)).GetProperty("error").GetProperty("code").GetString());
        var project = NewProject(host.DataDirectory);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/jobs", new { project, scale = new[] { 1.0 } })).StatusCode);
    }

    [Fact]
    public async Task ThePlanCarriesWhatWasLeftWhenTheRunStarted()
    {
        using var release = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s =>
            s.AddSingleton<IBuildStages>(new FakeBuild(() => new Stage[] { new WaitingStage(3, release) })));
        var project = NewProject(host.DataDirectory);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/open", new { path = project })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/plan")).GetProperty("atStart").ValueKind);

        var started = await Json(await host.Client.PostAsJsonAsync("/api/jobs", new { project, workers = 1 }));
        var atStart = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/plan")).GetProperty("atStart");
        Assert.Equal(started.GetProperty("runId").GetString(), atStart.GetProperty("runId").GetString());
        // every row and part of the table, with what it had left (the range is empty here)
        Assert.Equal(0, atStart.GetProperty("remaining").GetProperty("visit").GetInt32());
        Assert.Equal(0, atStart.GetProperty("remaining").GetProperty("visit.shot").GetInt32());

        release.Set();
        await host.App.Services.GetRequiredService<JobManager>().WaitAsync();
        // the last run's numbers stay (the job list keeps showing what it did)
        var after = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/plan")).GetProperty("atStart");
        Assert.Equal(started.GetProperty("runId").GetString(), after.GetProperty("runId").GetString());
    }

    [Fact]
    public void CommandLineBuildRunsAsAJobAndRefusesABusyWorkFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "cli.fxmapgen.json");
            var o = new StringWriter();
            var e = new StringWriter();
            Assert.Equal(0, CliCommands.Run(new[] { "new", file, "--maps", "roadmap" }, o, e));
            Assert.Equal(0, CliCommands.Run(new[] { "build", file, "--no-game" }, o, e));   // never the game of this PC in a test
            Assert.Contains("run folder: ", o.ToString());
            Assert.Contains("roadmap: waiting for the road shapes", o.ToString());
            Assert.Contains("game files: waiting for the road scans (1045 blocks", o.ToString());   // never the game files of this PC either
            Assert.Contains("road graph: waiting for the road scans (1045 blocks", o.ToString());
            Assert.Contains("landcover: waiting for the road graph", o.ToString());
            Assert.Single(Directory.GetDirectories(Path.Combine(dir, "logs"), "run-*"));

            using (WorkFolderLock.TryAcquire(new WorkFolder(dir), out _))
            {
                var err = new StringWriter();
                Assert.Equal(65, CliCommands.Run(new[] { "build", file, "--no-game" }, new StringWriter(), err));
                Assert.Contains("another run is using the work folder", err.ToString());
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
