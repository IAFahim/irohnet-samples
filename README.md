# irohnet-samples — peer-to-peer chat over iroh, from C#

A sample app for [N0.IrohNet.Safe](https://github.com/IsaMorphic/N0.IrohNet/pull/50) — safe C#/.NET bindings for [iroh](https://www.iroh.computer/). Two peers connect using nothing but a short ticket: same machine, another network, or both behind NATs. Relays, hole punching, and discovery are handled by iroh itself — no servers of your own, no port forwarding, no architecture.

The sample exercises the two transport lanes every iroh connection provides, which map directly onto what a multiplayer game or voice app needs:

| lane | used here | also good for |
|---|---|---|
| **reliable stream** (ordered, bidirectional) | chat lines | turn orders, world chat, file transfer — anything that must not be lost |
| **unreliable datagrams** (low latency) | periodic telemetry | player positions, Opus voice frames, heartbeats — where *latest* beats *complete* |

## Prerequisites

- .NET 10 SDK
- Rust toolchain ([rustup](https://rustup.rs)) — builds the native iroh library on first run
- Linux x64 is what this sample is tested on; the bindings also target macOS/Windows with the respective Rust toolchains

## Run it

```bash
git clone --recursive https://github.com/IAFahim/irohnet-samples.git
cd irohnet-samples

# terminal 1 (or another machine entirely):
./run.sh host

# it prints a ticket like:
#   endpointaauawcfp65sszr674nl35hjn3hgly75kypw54rrtevbbgdhncfvkcaybacwbcaabt7mqgaiavqjaaam73ebqcagavaalxh6zam
# terminal 2:
./run.sh join <ticket>
```

The first invocation compiles the native library (a few minutes); subsequent runs are incremental. `run.sh` passes `-p:TargetFrameworks=net10.0` because the bindings multi-target mobile platforms whose workloads are not needed here.

## What a session looks like

From an actual run (host left, join right):

```text
[net] node id: 11379fea…78                        [net] connecting...
[net] share this ticket with the peer:            [net] connected (negotiated alpn: irohnet-chat/1)
endpointaauawcfp65sszr674…                        [you] type a line and press enter…
[net] peer connected (negotiated alpn: irohnet-chat/1)
[peer] hello!
[dgram] ping 05:00:26 rtt=0ms loss=0.0%           [dgram] ping 05:00:28 rtt=10ms loss=0.0%
[peer] hey host!                                  [peer] hi join!
…                                                 quit
[net] peer left                                   [net] bye
[net] bye
```

Each side sends a datagram every two seconds carrying live connection stats (`Rtt`, `PacketLoss`) — the same shape as a game broadcasting player state, or a voice client sending compressed frames.

## Where to look in [`src/Chat/Program.cs`](src/Chat/Program.cs)

| concept | where |
|---|---|
| persistent node identity (secret key ↔ node id) | `LoadOrCreateIdentity` |
| bind an endpoint, get its ticket | `RunHostAsync` |
| connect from a ticket, anywhere | `RunJoinAsync` |
| reliable lane: open/accept a stream, framed lines | `ChatAsync` + `PrintPeerLinesAsync` |
| unreliable lane: send/receive datagrams | `SendTelemetryAsync` + `PrintDatagramsAsync` |
| live path stats | `SendTelemetryAsync` (reads `Rtt` / `PacketLoss` / see also `MaxDatagramSize`) |
| ordered teardown (helpers → stream FIN → connection → endpoint) | the `finally` in `ChatAsync`, plus the `await using` chains |

Two iroh-specific behaviors are worth noticing in the code:

- **The stream opener writes immediately** (`RunJoinAsync` sends `hello!` right after `OpenStreamAsync`). Waiting for the peer's accept before the first write would deadlock the handshake.
- **Datagrams are disposable by design**: keep payloads under `connection.MaxDatagramSize` (it follows the path MTU and can change during the connection's lifetime), and treat every datagram as send-state-not-commands. The safe layer queues received datagrams in a bounded drop-oldest buffer, so a slow consumer drops the *oldest* telemetry instead of growing memory — exactly what latest-state game traffic wants.

Identity is stored in `chat.key` (gitignored): keep the file and your node id — and therefore reachability — survives restarts; share it and you hand over the node, so treat it as a secret.

## Repository layout

- [`src/Chat`](src/Chat) — the sample (single `Program.cs`, no raw pointers, no `unsafe`)
- [`irohnet`](irohnet) — git submodule pinned to the N0.IrohNet commit containing `N0.IrohNet.Safe` ([PR #50](https://github.com/IsaMorphic/N0.IrohNet/pull/50)). Once the layer is merged and published to NuGet, the `ProjectReference` becomes an ordinary `PackageReference` and the submodule goes away.

## License

MIT — see [LICENSE](LICENSE).
