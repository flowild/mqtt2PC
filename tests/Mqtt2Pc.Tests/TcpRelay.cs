using System.Net;
using System.Net.Sockets;

namespace Mqtt2Pc.Tests;

// Closing both sides of this relay simulates a dropped network connection,
// without sending an MQTT DISCONNECT packet to the broker.
internal sealed class TcpRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _brokerPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private readonly object _sync = new();
    private TcpClient? _incoming;
    private TcpClient? _outgoing;

    public int Port { get; }

    public TcpRelay(int brokerPort)
    {
        _brokerPort = brokerPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _run = RunAsync();
    }

    public void DropConnection()
    {
        lock (_sync)
        {
            _incoming?.Dispose();
            _outgoing?.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var outgoing = new TcpClient();
                await outgoing.ConnectAsync(IPAddress.Loopback, _brokerPort, _stop.Token);
                lock (_sync) { _incoming = incoming; _outgoing = outgoing; }
                using var copyStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var toBroker = CopyAsync(incoming.GetStream(), outgoing.GetStream(), copyStop.Token);
                var fromBroker = CopyAsync(outgoing.GetStream(), incoming.GetStream(), copyStop.Token);
                await Task.WhenAny(toBroker, fromBroker);
                copyStop.Cancel();
                await Task.WhenAll(toBroker, fromBroker);
                lock (_sync) { _incoming = null; _outgoing = null; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private static async Task CopyAsync(Stream source, Stream target, CancellationToken token)
    {
        try { await source.CopyToAsync(target, token); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        DropConnection();
        await _run;
        _stop.Dispose();
    }
}
