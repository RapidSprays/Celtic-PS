using System.Net;
using System.Net.Sockets;

namespace CH.Patcher;

/// <summary>
/// Forwards the private client's HTTP traffic from 127.0.0.1:<see cref="Port"/> to the host's server.
/// <para>
/// The client's Unity build has "Allow downloads over HTTP" set to Not allowed, and Unity makes an exception only for
/// localhost. A friend's copy patched straight to the host's address (e.g. a Tailscale IP) has its very first request
/// refused ("Insecure connection not allowed") and hangs on the loading screen. So friends' copies are patched to this
/// relay instead, exactly like the host's own copy talks to 127.0.0.1, and the launcher keeps the relay running while
/// the game is open. The game's UDP connection doesn't go through Unity's HTTP stack and goes to the host directly.
/// </para>
/// <para>
/// Only the relay knows where the server is, so moving the server (off Tailscale, onto HTTPS) means changing the
/// relay's upstream, never re-patching the game.
/// </para>
/// </summary>
public sealed class LocalRelay : IDisposable
{
    /// <summary>The relay's port, and so the HTTP port friends' copies are patched to. Not 5555, so the kit also
    /// works on a machine that runs the server.</summary>
    public const int Port = 5565;

    public const string Address = "127.0.0.1";

    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _stop = new();

    private LocalRelay(TcpListener listener, string upstreamHost, int upstreamPort)
    {
        _listener = listener;
        _upstreamHost = upstreamHost.Trim('[', ']');
        _upstreamPort = upstreamPort;
    }

    /// <summary>Starts relaying. Throws <see cref="InvalidOperationException"/> when the port is taken.</summary>
    public static LocalRelay Start(string upstreamHost, int upstreamPort, int port = Port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException($"Celtic Heroes Private seems to be running already (port {port} is in use). Close the game, then try again.");
        }
        var relay = new LocalRelay(listener, upstreamHost, upstreamPort);
        _ = relay.AcceptLoop();
        return relay;
    }

    public int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Pipe(client);
        }
    }

    private async Task Pipe(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stop.Token);
                await Task.WhenAll(Copy(client, upstream), Copy(upstream, client));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The server went away or the game closed the connection: the game retries or shows its own error.
            }
        }
    }

    /// <summary>Copies one direction; when <paramref name="from"/> finishes sending, so does the relay towards <paramref name="to"/>.</summary>
    private async Task Copy(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream(), _stop.Token);
            to.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // One side failed: drop both, so the other copy ends too.
            from.Close();
            to.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
