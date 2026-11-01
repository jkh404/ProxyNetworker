# Virtual Network Transport Protocols

Virtual Network definitions support two transport protocols:

- `udp`: encrypted datagrams with direct-path probing and relay fallback. This is the default.
- `quic`: a reliable encrypted QUIC stream relay.

The protocol is selected when the virtual network is created and is returned to clients by the token configuration endpoint. QUIC currently uses the server relay; direct peer paths remain available only to UDP.

## Benchmark

Test date: 2026-11-01

Topology: `<node37-public-ip>` to `<server-public-ip>`, relay-only, same server process. Each protocol used three rounds of 100 ICMP echo requests after warm-up.

| Protocol | Samples | Loss | Min | Avg | P50 | P95 | P99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| UDP | 300 | 0% | 0.64 ms | 7.27 ms | 1.26 ms | 1.97 ms | 250 ms | 453 ms |
| QUIC | 300 | 0% | 1.47 ms | 33.38 ms | 2.31 ms | 260 ms | 719 ms | 799 ms |

KCP was evaluated with `NoDelay`, fast resend, a 1 ms update interval, and congestion control disabled. Its relay-only benchmark had a P50 latency of 4237 ms and a P95 latency of 11439 ms, so KCP support was removed. Existing KCP network records are automatically disabled during upgrade.

## Recommendation

Keep `udp` as the default protocol. It has the lowest median and tail latency and retains direct-path selection. Offer `quic` as the reliable alternative when packet loss must be hidden.

Linux x64 deployments need `libmsquic.so.2` and `libnuma.so.1` available through `LD_LIBRARY_PATH`. Linux arm64 installations can use the distribution `libmsquic` package.
