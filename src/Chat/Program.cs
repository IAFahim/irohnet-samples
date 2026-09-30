// Peer-to-peer chat over iroh, using N0.IrohNet.Safe (C#/.NET bindings for iroh).
//
//   chat host            prints a one-time ticket and waits for a peer to connect
//   chat join <ticket>   connects to that ticket from anywhere: same LAN, another network,
//                        or across NATs — relays and hole punching are handled by iroh
//
// The sample exercises both transport lanes an iroh connection provides, which map to the
// two kinds of traffic a multiplayer game or voice app needs:
//
//   * reliable, ordered, bidirectional streams  -> chat lines (also: world chat, turn orders,
//     file transfers, anything that must not be lost)
//   * unreliable, low-latency datagrams         -> periodic telemetry (also: player positions,
//     voice frames, anything where "latest" beats "complete")
//
// Identity persists in chat.key, so the node id (and thus discovery for it) survives restarts.

using System.Text;
using System.Threading.Channels;
using N0.IrohNet;

const string Alpn = "irohnet-chat/1";

switch (args)
{
    case ["host"]:
        return await RunSafelyAsync(RunHostAsync);
    case ["join", var ticket]:
        return await RunSafelyAsync(() => RunJoinAsync(ticket));
    default:
        Console.WriteLine("usage: chat host | chat join <ticket>");
        return args.Length == 0 ? 0 : 1;
}

static async Task<int> RunSafelyAsync(Func<Task<int>> run)
{
    try
    {
        return await run();
    }
    catch (IrohException exception)
    {
        // Tickets are typed by hand; a typo or an unreachable/offline node surfaces here.
        Console.WriteLine($"[err] {exception.Message.TrimEnd('.')}");
        return 1;
    }
}

static IrohEndpointOptions EndpointOptions(IrohSecretKey key) => new()
{
    // n0's public relays as a fallback path when direct hole punching fails, plus DNS + mDNS
    // discovery. Together these let two endpoints find each other from just the ticket.
    RelayMode = IrohRelayMode.Default,
    Discovery = IrohDiscoveryConfig.All,
    Alpns = new[] { Encoding.UTF8.GetBytes(Alpn) },
    SecretKey = key,
};

static async Task<int> RunHostAsync()
{
    using IrohSecretKey key = LoadOrCreateIdentity();
    await using IrohEndpoint endpoint = await IrohEndpoint.BindAsync(EndpointOptions(key));

    Console.WriteLine($"[net] node id: {key.PublicKey.AsBase32()}");
    Console.WriteLine("[net] share this ticket with the peer:");
    Console.WriteLine(endpoint.LocalAddr().Ticket);
    Console.WriteLine("[net] waiting for a peer to connect...");

    await using IrohConnection connection = await endpoint.AcceptAsync();
    Console.WriteLine($"[net] peer connected (negotiated alpn: {DescribeAlpn(connection.NegotiatedAlpn)})");

    await using IrohStream stream = await connection.AcceptStreamAsync();
    return await ChatAsync(connection, stream);
}

static async Task<int> RunJoinAsync(string ticket)
{
    using IrohSecretKey key = LoadOrCreateIdentity();
    await using IrohEndpoint endpoint = await IrohEndpoint.BindAsync(EndpointOptions(key));

    Console.WriteLine("[net] connecting...");
    await using IrohConnection connection = await endpoint.ConnectAsync(
        IrohNodeAddr.Parse(ticket), Encoding.UTF8.GetBytes(Alpn));
    Console.WriteLine($"[net] connected (negotiated alpn: {DescribeAlpn(connection.NegotiatedAlpn)})");

    Stream stream = await connection.OpenStreamAsync();

    // Contract from the native layer: the side that opens the stream must write its first bytes
    // immediately; waiting for the peer to accept before the first write deadlocks the handshake.
    await stream.WriteAsync(Encoding.UTF8.GetBytes("hello!\n"));

    return await ChatAsync(connection, (IrohStream)stream);
}

static async Task<int> ChatAsync(IrohConnection connection, IrohStream stream)
{
    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true; // handle Ctrl+C gracefully instead of tearing the process down
        cts.Cancel();
    };

    // Readers/writers keep the stream open; the stream itself is disposed explicitly below so
    // the teardown order stays visible: helpers -> stream (FIN) -> connection -> endpoint.
    // The encoding is BOM-less so peer lines print exactly as typed.
    using StreamReader reader = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
    using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

    // Console.In has no truly asynchronous reads, so one dedicated pump parks on it and hands
    // lines to the main loop through a channel; that keeps Ctrl+C/peer-left cancellation live.
    Channel<string?> consoleLines = Channel.CreateUnbounded<string?>();
    Task consolePump = Task.Run(async () =>
    {
        while (true)
        {
            string? line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                consoleLines.Writer.TryComplete(); // stdin closed: treat like 'quit'
                return;
            }

            await consoleLines.Writer.WriteAsync(line);
        }
    });

    Task peerLines = Task.Run(() => PrintPeerLinesAsync(reader, cts));
    Task datagrams = Task.Run(() => PrintDatagramsAsync(connection, cts));
    Task telemetry = Task.Run(() => SendTelemetryAsync(connection, cts.Token));

    Console.WriteLine("[you] type a line and press enter ('quit' or Ctrl+C to leave)");

    try
    {
        while (!cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await consoleLines.Reader.ReadAsync(cts.Token);
            }
            catch (ChannelClosedException)
            {
                break;
            }

            if (line is null or "quit" or "exit")
            {
                break;
            }

            if (line.Length > 0)
            {
                await writer.WriteLineAsync(line);
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        // Cancel the helpers first, then close the stream: send_stream_finish emits a graceful
        // FIN, so the peer's reader sees end-of-stream and shuts down cleanly as well. The
        // console pump is intentionally NOT awaited: with an interactive terminal it stays
        // parked on stdin until the process exits, and awaiting it would hang teardown.
        cts.Cancel();
        await Quiet(peerLines, datagrams, telemetry);
        await stream.DisposeAsync();
    }

    Console.WriteLine("[net] bye");
    return 0;
}

static async Task PrintPeerLinesAsync(StreamReader reader, CancellationTokenSource cts)
{
    CancellationToken token = cts.Token;
    try
    {
        while (!token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(token);
            if (line is null)
            {
                Console.WriteLine("[net] peer left");
                cts.Cancel(); // peer hung up: unwind the local loops too
                return;
            }

            Console.WriteLine($"[peer] {line}");
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
    catch (IOException)
    {
    }
}

static async Task PrintDatagramsAsync(IrohConnection connection, CancellationTokenSource cts)
{
    CancellationToken token = cts.Token;
    try
    {
        while (!token.IsCancellationRequested)
        {
            // ReceiveDatagramAsync waits for the next unreliable datagram; the safe layer pumps
            // them into a bounded queue (oldest dropped when full) so a slow consumer cannot
            // exhaust memory — the right shape for latest-state game traffic.
            byte[] datagram = await connection.ReceiveDatagramAsync(token);
            Console.WriteLine($"[dgram] {Encoding.UTF8.GetString(datagram)}");
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
    catch (IrohException)
    {
        // The connection closed while we were waiting; shutdown handles the rest.
    }
}

static async Task SendTelemetryAsync(IrohConnection connection, CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            // Datagrams are bounded by the path MTU (see connection.MaxDatagramSize); keep payloads
            // small and treat them as disposable: send state, never commands.
            string status =
                $"ping {DateTimeOffset.UtcNow:HH:mm:ss} rtt={connection.Rtt.TotalMilliseconds:F0}ms loss={connection.PacketLoss:P1}";
            await connection.SendDatagramAsync(Encoding.UTF8.GetBytes(status), token);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
    catch (IrohException)
    {
    }
}

static IrohSecretKey LoadOrCreateIdentity()
{
    const string keyFile = "chat.key";
    if (File.Exists(keyFile))
    {
        IrohSecretKey key = IrohSecretKey.FromBase32(File.ReadAllText(keyFile).Trim());
        Console.WriteLine($"[key] loaded identity: {key.PublicKey.AsBase32()}");
        return key;
    }

    IrohSecretKey fresh = IrohSecretKey.Generate();
    File.WriteAllText(keyFile, fresh.AsBase32());
    Console.WriteLine($"[key] generated identity: {fresh.PublicKey.AsBase32()} (stored in {keyFile}, keep it secret)");
    return fresh;
}

static string DescribeAlpn(byte[]? alpn) => alpn is null ? "(none)" : Encoding.UTF8.GetString(alpn);

static async Task Quiet(params Task[] tasks)
{
    foreach (Task task in tasks)
    {
        try
        {
            await task;
        }
        catch
        {
            // Helper tasks are expected to end through cancellation or connection teardown.
        }
    }
}
