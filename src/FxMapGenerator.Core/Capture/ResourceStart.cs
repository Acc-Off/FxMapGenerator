using System.Text.RegularExpressions;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// Starts the capture resource from the game console when it does not answer: <c>refresh</c> (the server finds a folder
/// put there since it started), then <c>ensure</c> by its name, then it asks again. Both are server commands, which the
/// server takes from a player with the permission for them. The console lines of the attempt are kept: the server's own
/// words come back to the client's console, and they tell why it did not start.
/// </summary>
public sealed partial class ResourceStart(IGameAccess game)
{
    public const string ResourceName = "fxmapgen-capture";

    /// <param name="State"><c>noConsole</c>, <c>running</c> (it answered at once), <c>started</c> (it answered after refresh and ensure), <c>notStarted</c>.</param>
    /// <param name="Reason">
    /// Why it did not start (<c>notStarted</c> only): <c>notFound</c> = the server said it has no resource of that name (the
    /// folder is not in its resources, or has another name), <c>denied</c> = the server refused the command, <c>noAnswer</c> =
    /// the server said nothing and the resource does not answer (most likely no permission to send server commands).
    /// </param>
    /// <param name="Console">The commands sent and the lines the console gave back during the attempt (without FiveM's colour codes).</param>
    public sealed record Result(string State, HelloReply? Hello, IReadOnlyList<string> Console, string? Reason = null);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>After refresh and after ensure (the server scans its resources, then starts the resource and its client side).</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public async Task<Result> RunAsync(string host, int port, CancellationToken token = default)
    {
        using var transcript = new StringWriter();
        HelloReply? hello;
        string state;
        await using (var link = new GameLink(game.OpenConsole(host, port), transcript) { HelloTimeout = HelloTimeout })
        {
            if (!await link.ConnectAsync(ConnectTimeout, token).ConfigureAwait(false)) return new Result("noConsole", null, []);
            hello = await link.HelloAsync(token).ConfigureAwait(false);
            if (hello is not null) state = "running";
            else
            {
                await link.SendAsync("refresh", token).ConfigureAwait(false);
                await link.PauseAsync(Settle, token).ConfigureAwait(false);
                await link.SendAsync("ensure " + ResourceName, token).ConfigureAwait(false);
                await link.PauseAsync(Settle, token).ConfigureAwait(false);
                hello = await link.HelloAsync(token).ConfigureAwait(false);
                state = hello is null ? "notStarted" : "started";
            }
        }
        var lines = transcript.ToString().Split('\n').Select(l => ColourCode().Replace(l.TrimEnd('\r'), "")).Where(l => l.Length > 0).ToList();
        return new Result(state, hello, lines, state == "notStarted" ? ReasonOf(lines) : null);
    }

    /// <summary>
    /// Why the resource did not start, from what the server said to the console: "Couldn't find resource" (seen on a
    /// Qbox server for a name it does not have), a refusal of the command, or nothing at all.
    /// </summary>
    public static string ReasonOf(IEnumerable<string> console)
    {
        var said = console.ToList();
        if (said.Any(l => l.Contains("Couldn't find resource " + ResourceName, StringComparison.OrdinalIgnoreCase))) return "notFound";
        if (said.Any(l => l.Contains("Access denied", StringComparison.OrdinalIgnoreCase))) return "denied";
        return "noAnswer";
    }

    /// <summary>FiveM's colour codes in console text (<c>^0</c> .. <c>^9</c>).</summary>
    [GeneratedRegex(@"\^\d")]
    private static partial Regex ColourCode();
}
