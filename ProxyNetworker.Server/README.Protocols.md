# Virtual Network Transport Protocols

Virtual Network definitions support two transport protocols:

- `udp`: authenticated datagrams with latency-aware mesh routing, multi-hop forwarding, and relay fallback. AES-GCM is the default; deployments can opt into HMAC-only confidentiality-off mode while retaining tamper detection.
- `quic`: a reliable encrypted QUIC stream relay.

The protocol is selected when the virtual network is created and is returned to clients by the token configuration endpoint. QUIC currently uses the server relay. Direct and multi-hop peer paths are available only to UDP.

## Mesh Routing

UDP nodes exchange candidate endpoints and link-state advertisements through the server control path. Each directed link carries EWMA RTT, jitter, loss, and a hop penalty. Nodes build the lowest-cost route to every reachable peer and use an 8-hop limit with loop prevention. A node may forward traffic for another peer, and the server relay remains the final fallback when no healthy mesh route exists.

See [UDP 网状路由架构](../docs/MESH_ROUTING.md) for the control-plane format, cost formula, forwarding behavior, security boundary, and test coverage.

## Benchmark

Test date: 2026-11-01

Topology: `<client-node-public-ip>` to `<server-node-public-ip>`, relay-only, same server process. Each protocol used three rounds of 100 ICMP echo requests after warm-up. These figures measure server relay behavior and do not represent UDP direct or multi-hop mesh latency.

| Protocol | Samples | Loss | Min | Avg | P50 | P95 | P99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| UDP | 300 | 0% | 0.64 ms | 7.27 ms | 1.26 ms | 1.97 ms | 250 ms | 453 ms |
| QUIC | 300 | 0% | 1.47 ms | 33.38 ms | 2.31 ms | 260 ms | 719 ms | 799 ms |

KCP was evaluated with `NoDelay`, fast resend, a 1 ms update interval, and congestion control disabled. Its relay-only benchmark had a P50 latency of 4237 ms and a P95 latency of 11439 ms, so KCP support was removed. Existing KCP network records are automatically disabled during upgrade.

## Recommendation

Keep `udp` as the default protocol. It has the lowest measured relay latency and additionally supports direct and multi-hop mesh paths. Offer `quic` as the reliable alternative when packet loss must be hidden.

Linux x64 deployments need `libmsquic.so.2`, `libnuma.so.1`, and a compatible `libcrypto.so.1.1` available through `LD_LIBRARY_PATH`. Linux arm64 installations can use the distribution `libmsquic` package.

## Optional Encryption

Virtual Network definitions expose `EncryptionEnabled`, defaulting to `true`.

- Enabled: version 2 frames use AES-256-GCM for confidentiality, authenticity, and replay-window validation.
- Disabled: version 3 frames keep payloads in plaintext but append an HMAC-SHA256 tag (first 16 bytes) and perform constant-time verification. The token-derived session key is still required, so this is not an unauthenticated plaintext mode.

Changing the mode restarts the network runtime. Clients detect the new value during configuration refresh, rebuild their TUN runtime automatically, and negotiate the matching frame version.

## Linux Batched Data Path

Linux UDP transports probe for `recvmmsg` and `sendmmsg` and use a batch size of 32 when available. The receive path reuses pinned packet buffers; the send path retries partial `sendmmsg` results under backpressure. Other platforms and unsupported libc implementations keep the existing single-datagram path.

Linux TUN probes `IFF_VNET_HDR`, `TUNSETVNETHDRSZ`, `TUNSETOFFLOAD`, and TSO4 support. When available it:

- completes ordinary packets that carry `VIRTIO_NET_HDR_F_NEEDS_CSUM`;
- splits incoming TCPv4 GSO frames into MTU-sized packets;
- preserves terminal `PSH/FIN` only on the last segmented packet;
- coalesces contiguous TCP segments into GSO writes with per-segment checksum completion.

If any capability is unavailable or metadata is malformed, the client logs the reason and falls back without changing the virtual IP or token. Linux hosts under sustained high throughput should allow larger UDP socket buffers:

```conf
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
```

The client requests 4 MiB socket buffers when the host policy permits it. See [the Linux performance report](../docs/2026-11-01-linux-batch-gso-report.md) for the measured results.
