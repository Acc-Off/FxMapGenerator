using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace FxMapGenerator.App.Web;

/// <summary>
/// Server-sent events for <c>GET /api/events</c>. Every subscriber gets its own bounded channel; slow readers drop
/// the oldest events (the UI re-fetches state on every event anyway, so losing an intermediate one is harmless).
/// </summary>
public sealed class EventHub
{
    public const string Path = "/api/events";
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    readonly object _sync = new();
    readonly List<Channel<(string name, string json)>> _subscribers = new();
    readonly JsonSerializerOptions _json;

    public EventHub(JsonSerializerOptions json) => _json = json;

    public int SubscriberCount { get { lock (_sync) return _subscribers.Count; } }

    /// <summary>Broadcasts <paramref name="payload"/> as the SSE event <paramref name="name"/>.</summary>
    public void Publish<T>(string name, T payload)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        lock (_sync)
        {
            foreach (var ch in _subscribers) ch.Writer.TryWrite((name, json));
        }
    }

    /// <summary>Streams events to one client until it disconnects or the app stops.</summary>
    public async Task ServeAsync(HttpContext context, Func<IEnumerable<(string name, object payload)>> initial, CancellationToken stopping)
    {
        var channel = Channel.CreateBounded<(string name, string json)>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_sync) _subscribers.Add(channel);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var ct = linked.Token;
        try
        {
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes("retry: 2000\n\n"), ct);
            foreach (var (name, payload) in initial())
                await WriteEventAsync(response, name, JsonSerializer.Serialize(payload, _json), ct);
            await response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HeartbeatInterval);
                try
                {
                    var (name, json) = await channel.Reader.ReadAsync(timeout.Token);
                    await WriteEventAsync(response, name, json, ct);
                    while (channel.Reader.TryRead(out var more)) await WriteEventAsync(response, more.name, more.json, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.Body.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"), ct);
                }
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            lock (_sync) _subscribers.Remove(channel);
        }
    }

    static async Task WriteEventAsync(HttpResponse response, string name, string json, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("event: ").Append(name).Append('\n');
        foreach (var line in json.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
    }
}
