using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Encoding.Api.Mcp;

/// <summary>
/// Gère les sessions SSE actives pour les clients connectés via le transport HTTP/SSE de MCP.
/// </summary>
public sealed class McpSessionManager
{
    private readonly ConcurrentDictionary<string, Channel<McpJsonRpcResponse>> _sessions = new(StringComparer.Ordinal);

    public ChannelReader<McpJsonRpcResponse> CreateSession(string sessionId)
    {
        var channel = Channel.CreateUnbounded<McpJsonRpcResponse>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _sessions[sessionId] = channel;
        return channel.Reader;
    }

    public async Task<bool> SendMessageAsync(string sessionId, McpJsonRpcResponse message, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var channel))
        {
            await channel.Writer.WriteAsync(message, ct);
            return true;
        }
        return false;
    }

    public void CloseSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
