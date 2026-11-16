using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace EchoDeck;

/// <summary>What Discord shows under the user's name.</summary>
internal sealed record DiscordActivity(string Details, string State, long? StartUnix, string? LargeImage, string? LargeText);

/// <summary>
/// A small client for Discord's local "rich presence" connection (a named pipe the Discord app opens on this PC): no package and no
/// login. It needs an application ID (create an app at discord.com/developers, copy its Application ID). Nothing is sent anywhere but
/// to the Discord app on the same PC. Updates are queued and only the latest one is sent, at most one every 1.5 seconds. If Discord
/// is not running, updates are quietly dropped and it tries again with the next one.
/// </summary>
internal sealed class DiscordRpc : IDisposable
{
    private readonly string _clientId;
    private readonly object _lock = new();
    private NamedPipeClientStream? _pipe;
    private DiscordActivity? _pending;
    private bool _hasPending;
    private bool _running;
    private bool _disposed;

    public DiscordRpc(string clientId) => _clientId = clientId.Trim();

    public string ClientId => _clientId;

    /// <summary>Shows the activity; null removes it. Returns at once.</summary>
    public void Post(DiscordActivity? activity)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pending = activity;
            _hasPending = true;
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            DiscordActivity? activity;
            lock (_lock)
            {
                if (!_hasPending || _disposed) { _running = false; return; }
                activity = _pending;
                _hasPending = false;
            }

            try { await SendAsync(activity).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLog.Write("Discord", ex);
                Disconnect();   // Discord closed or restarted: connect again with the next update
            }
            await Task.Delay(1500).ConfigureAwait(false);   // Discord allows only a few updates every 20 seconds
        }
    }

    private async Task SendAsync(DiscordActivity? activity)
    {
        if (_pipe is not { IsConnected: true }) await ConnectAsync().ConfigureAwait(false);
        if (_pipe == null) return;   // Discord is not running

        var args = new Dictionary<string, object?> { ["pid"] = Environment.ProcessId };
        if (activity != null)
        {
            var assets = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(activity.LargeImage)) assets["large_image"] = activity.LargeImage!;
            if (!string.IsNullOrEmpty(activity.LargeText)) assets["large_text"] = activity.LargeText!;

            var data = new Dictionary<string, object?>
            {
                ["details"] = Limit(activity.Details),
                ["assets"] = assets,
            };
            if (activity.State.Length >= 2) data["state"] = Limit(activity.State);
            if (activity.StartUnix is long start) data["timestamps"] = new Dictionary<string, long> { ["start"] = start };
            args["activity"] = data;
        }

        var message = new Dictionary<string, object?>
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            ["nonce"] = Guid.NewGuid().ToString(),
        };
        await WriteFrameAsync(1, JsonSerializer.Serialize(message)).ConfigureAwait(false);
        await ReadFrameAsync().ConfigureAwait(false);   // the answer: read so that the pipe never fills up
    }

    private static string Limit(string text) => text.Length <= 120 ? text : text.Substring(0, 117) + "...";

    private async Task ConnectAsync()
    {
        Disconnect();
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var cts = new CancellationTokenSource(600);
                await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                continue;
            }

            _pipe = pipe;
            try
            {
                await WriteFrameAsync(0, JsonSerializer.Serialize(new { v = 1, client_id = _clientId })).ConfigureAwait(false);
                await ReadFrameAsync().ConfigureAwait(false);   // READY (or an error for a wrong application ID)
                return;
            }
            catch
            {
                Disconnect();
            }
        }
    }

    private void Disconnect()
    {
        var pipe = _pipe;
        _pipe = null;
        try { pipe?.Dispose(); } catch { }
    }

    // A frame is: opcode (4 bytes), length (4 bytes, little endian), then that many bytes of JSON.
    private async Task WriteFrameAsync(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(opcode).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame).ConfigureAwait(false);
        await _pipe.FlushAsync().ConfigureAwait(false);
    }

    private async Task<string> ReadFrameAsync()
    {
        using var cts = new CancellationTokenSource(4000);
        var header = new byte[8];
        await ReadExactAsync(header, cts.Token).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header, 4);
        if (length < 0 || length > 1_000_000) throw new InvalidDataException("Unexpected answer from Discord.");
        var body = new byte[length];
        await ReadExactAsync(body, cts.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _pipe!.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Discord closed the connection.");
            read += n;
        }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
        Disconnect();
    }
}
