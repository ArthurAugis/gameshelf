using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

namespace GameShelf.Launchers;

/// <summary>
/// Talks to the Chromium Embedded Framework inside a launcher (Steam, Epic Games) through its remote debugging
/// interface: the list of pages is an HTTP endpoint, and a script is run in a page over a web socket
/// (Chrome DevTools Protocol, <c>Runtime.evaluate</c>). Used by the readers of launchers that keep their library
/// in their own web UI.
/// </summary>
internal static class Cdp
{
    /// <summary>The web socket address of the first page for which <paramref name="isTheOne"/> is true, or null.</summary>
    public static async Task<string?> FindPageAsync(int port, Func<JsonElement, bool> isTheOne)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient();
            using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json", timeout.Token));
            foreach (var page in pages.RootElement.EnumerateArray())
                if (isTheOne(page) && page.TryGetProperty("webSocketDebuggerUrl", out var url)) return url.GetString();
            return null;
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="script"/> in the page (an async script is awaited) and returns the string it returns.
    /// Null when the page is gone, the script threw, or it returned something other than a string.
    /// </summary>
    public static async Task<string?> RunAsync(string script, string socketUrl, TimeSpan timeoutAfter)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutAfter);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(socketUrl), timeout.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression = script, returnByValue = true, awaitPromise = true },
            });
            await socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token);

            using var reply = JsonDocument.Parse(await ReceiveMessageAsync(socket, timeout.Token));
            var value = reply.RootElement.GetProperty("result").GetProperty("result").GetProperty("value");
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception e) when (IsConnectionFailure(e))
        {
            return null;
        }
    }

    // The launcher is not running, its debug mode is off, the port is closed, the page is not ready, or the script threw.
    static bool IsConnectionFailure(Exception e) =>
        e is HttpRequestException or WebSocketException or JsonException
            or KeyNotFoundException or InvalidOperationException or OperationCanceledException;

    static async Task<byte[]> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var buffer = new byte[65536];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancel);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }
}
