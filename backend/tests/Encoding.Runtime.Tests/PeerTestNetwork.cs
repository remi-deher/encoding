using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Encoding.Runtime.Tests;

/// <summary>CONNECT proxies preserve end-to-end mTLS while imposing a bidirectional partition on one peer.</summary>
internal sealed class PeerTestNetwork : IAsyncDisposable
{
    private readonly int[] peerPorts;
    private readonly TcpListener[] listeners;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Guid, (int Source, int Target, TcpClient Client, TcpClient Remote)> connections = new();
    private readonly ConcurrentDictionary<int, byte> isolated = new();
    private readonly Task[] accepting;
    public int ProxyPort(int index) => ((IPEndPoint)listeners[index].LocalEndpoint).Port;
    public PeerTestNetwork(int[] peerPorts)
    {
        this.peerPorts = peerPorts;
        listeners = peerPorts.Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var listener in listeners) listener.Start();
        accepting = listeners.Select((listener, index) => AcceptAsync(listener, index)).ToArray();
    }
    public void Isolate(int index)
    {
        isolated[index] = 0;
        foreach (var pair in connections.Values.Where(pair => pair.Source == index || pair.Target == index))
        { pair.Client.Dispose(); pair.Remote.Dispose(); }
    }
    public void Reconnect(int index) => isolated.TryRemove(index, out _);
    private async Task AcceptAsync(TcpListener listener, int source)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                _ = RelayAsync(client, source);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }
    private async Task RelayAsync(TcpClient client, int source)
    {
        var id = Guid.NewGuid();
        using (client)
        using (var remote = new TcpClient())
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var incoming = client.GetStream();
                var header = new StringBuilder();
                var single = new byte[1];
                while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await incoming.ReadAsync(single, timeout.Token) == 0) return;
                    header.Append((char)single[0]);
                }
                var first = header.ToString().Split("\r\n")[0].Split(' ');
                if (first.Length != 3 || first[0] != "CONNECT") return;
                var endpoint = new Uri("https://" + first[1]);
                var target = Array.IndexOf(peerPorts, endpoint.Port);
                if (endpoint.Host != "127.0.0.1" || target < 0 || isolated.ContainsKey(source) || isolated.ContainsKey(target))
                {
                    await incoming.WriteAsync("HTTP/1.1 503 Partitioned\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), timeout.Token);
                    return;
                }
                await remote.ConnectAsync(IPAddress.Loopback, endpoint.Port, timeout.Token);
                connections[id] = (source, target, client, remote);
                await incoming.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
                var outgoing = remote.GetStream();
                await Task.WhenAny(incoming.CopyToAsync(outgoing, timeout.Token), outgoing.CopyToAsync(incoming, timeout.Token));
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { connections.TryRemove(id, out _); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        foreach (var listener in listeners) listener.Stop();
        foreach (var connection in connections.Values) { connection.Client.Dispose(); connection.Remote.Dispose(); }
        await Task.WhenAll(accepting);
        stopping.Dispose();
    }
}
