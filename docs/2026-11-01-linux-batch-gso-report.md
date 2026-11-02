# Linux UDP Batch and TUN GSO Report

Test date: 2026-11-01

## Scope

This report validates the Linux virtual-network data path after adding:

- UDP `recvmmsg`/`sendmmsg` batching with a batch size of 32;
- 4 MiB requested socket buffers and retry on partial `sendmmsg` submission;
- TUN `vnet_hdr` with TCPv4 TSO;
- completion of non-GSO checksum-offload packets;
- segmentation of terminal TSO packets with `PSH/FIN` preserved only on the final segment;
- optional AES-GCM and HMAC-only authenticated frame modes.

The test did not use WSL. Both data-plane clients ran as native Linux AOT binaries. The source node and download node were separate hosts in the same private network; the virtual path selected a direct UDP link rather than the public relay.

## File Transfer

Method: one 256 MiB file, HTTP/1.1, a fresh connection per run, four runs per mode, no concurrent test traffic. All reported virtual-network runs returned the complete `268435456` bytes.

| Mode | Run 1 | Run 2 | Run 3 | Run 4 | Average |
| --- | ---: | ---: | ---: | ---: | ---: |
| Private LAN direct | 62.66 MB/s | 62.67 MB/s | 62.66 MB/s | 62.65 MB/s | 62.66 MB/s |
| UDP virtual network, AES-GCM | 37.87 MB/s | 37.95 MB/s | 38.11 MB/s | 39.65 MB/s | 38.39 MB/s |

The virtual-network path reached about 61% of the ordinary same-LAN TCP download rate. Compared with the earlier build measured at roughly 15-16 MB/s in this environment, throughput improved by about 2.4x.

A single full-file HMAC-only run completed at 35.61 MB/s. The difference from the AES-GCM average is small relative to network and scheduling variance; this workload is dominated by packet forwarding rather than AES-GCM throughput.

## Correctness Checks

- Both Linux clients logged `vnet_hdr=True, gso=True`.
- Both clients logged `recvmmsg/sendmmsg` batch I/O as enabled.
- ICMP passed at the configured 1400-byte MTU.
- The HMAC-only switch was observed by both clients and caused automatic runtime reconnection without changing tokens or virtual IPs.
- Restoring AES-GCM caused both clients to reconnect automatically and report `encryption=AES-GCM`.
- Tampering tests reject modified payloads, nonces/tags, and authentication tags in both frame modes.

## Kernel Buffers

Before tuning, both nodes reported `net.core.rmem_max=212992`. Under load, UDP receive-buffer counters increased substantially even though the client requested 4 MiB buffers. Setting `rmem_max` and `wmem_max` to 16 MiB raised each actual socket receive buffer to 8 MiB after the kernel's internal accounting. The final four virtual-network runs then completed without new receive-buffer errors.

Recommended persistent setting on Linux data nodes:

```conf
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
```

## Fallback Behavior

The implementation does not make GSO or batched syscalls mandatory. Missing symbols, unsupported socket families, TUN ioctl failures, malformed checksum metadata, or unsupported GSO types take the existing safe path or are rejected with a log message. `PN_DISABLE_GSO_TX=1` disables only outbound TUN coalescing for troubleshooting.

## Follow-up Hot-Path Optimization

The next pass removed three per-packet costs without changing the wire protocol:

- pooled packet frames now transfer ownership directly to the Linux send queue instead of being copied into a second pooled buffer;
- forwarding decisions for the same destination are reused for 250 ms, and route candidate selection uses allocation-free loops instead of LINQ;
- encrypted frames use a per-thread random 64-bit nonce prefix plus a 32-bit counter instead of requesting fresh random bytes for every packet. The prefix is regenerated when the counter wraps.

An 8,000-frame, multi-thread test verifies that generated nonces are unique. A `perf` sample before this pass showed OpenSSL `RAND_bytes_ex -> EVP_RAND_generate -> getpid` on the per-packet path; after the change, that stack disappeared from the profile.

### Follow-up Throughput

Method remained 256 MiB over HTTP/1.1 with a fresh connection per run. Nine complete runs were collected:

| Metric | Previous build | Optimized build |
| --- | ---: | ---: |
| Mean | 38.39 MB/s | 40.32 MB/s |
| Median | 38.67 MB/s | 40.05 MB/s |
| Best run | 40.07 MB/s | 43.73 MB/s |

Median throughput improved by about 3.6%, with the best run about 9% above the previous best. Network and route variation still exceed the small average gain in some runs, so the median is the more useful comparison.

The user-space CPU share during a representative transfer dropped from roughly 36-40% to 22-29%; total process CPU was roughly 60-70% instead of 74-86%. The remaining dominant samples are Linux UDP/IP transmit/receive processing and OpenSSL error-stack bookkeeping around AES-GCM, not random-number generation or route selection.

## Second Follow-up: Route Maintenance and Mesh Buffers

The next source-level pass keeps the frame versions, authentication modes, MTU, and UDP batching protocol unchanged:

- peer cleanup has a monotonic 250 ms gate, so ordinary packets no longer trigger full cleanup and route reconstruction;
- unchanged local links retain the existing route snapshot; link-state updates and expiry still rebuild it and invalidate the forwarding cache;
- client heartbeats also perform maintenance when there is no incoming traffic;
- mesh parsing retains a view of the authenticated receive buffer, and direct mesh packets are parsed once;
- originating and multi-hop tunnel packets construct their mesh payload inside the final pooled frame and authenticate/encrypt it in place, avoiding the intermediate mesh arrays;
- the startup AEAD interoperability check now covers in-place encryption/decryption, including the 1408-byte mesh payload, and retains the managed fallback;
- the Linux send queue wakes its consumer only when transitioning from empty to nonempty, and IPv4 socket-address serialization no longer allocates an address array.

Receive-buffer views are consumed synchronously or through the awaited TUN batch flush before the next receive call can reuse the buffers. Forwarding writes into a separate owned frame and does not mutate the incoming mesh buffer.

### Local Diagnostic Measurements

Environment: Windows, PowerShell running .NET 10.0.11, four-node synthetic topology, 10,000 calls, no network I/O. Both source states are uncommitted worktrees based on `4e9be1b`; the pre-change state already included the first follow-up optimizations.

| Operation | Elapsed | Allocated bytes/call |
| --- | ---: | ---: |
| Pre-change unconditional cleanup | 88.31 ms | 3736 |
| New cleanup during the throttle window | 0.11 ms | 0 |
| New cleanup with every maintenance sweep forced | 39.05 ms | 752 |

The throttled measurement fits entirely inside the 250 ms window and does not measure a full maintenance sweep. The forced-sweep measurement includes reflection to reset the gate. These numbers establish reduced user-space work, not a Linux throughput multiplier.

The current probe is reproducible after building the core tests:

```powershell
dotnet build ProxyNetworker.slnx -c Release --no-restore
pwsh -NoProfile -File scripts/Measure-VirtualNetworkCleanup.ps1
```

### Linux Deployment Re-test

The second follow-up was published as a Linux x64 Native AOT client and deployed to two native Linux nodes without using WSL. Both nodes kept their existing tokens and virtual IPs. The selected path was direct UDP, encryption was AES-GCM, socket batching remained enabled at 32 packets, Linux TUN reported `vnet_hdr=True, gso=True`, and the interface MTU was 1400.

File transfer used the same 256 MiB HTTP/1.1 workload and a fresh connection for every run. The ordinary private-LAN baseline used four runs; the virtual-network run used nine complete transfers.

| Mode | Runs | Mean | Median | Min | Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| Private LAN direct | 4 | 62.64 MB/s | 62.65 MB/s | 62.59 MB/s | 62.66 MB/s |
| UDP virtual network, AES-GCM | 9 | 41.50 MB/s | 41.49 MB/s | 39.38 MB/s | 43.81 MB/s |

The nine virtual-network runs were 41.79, 40.94, 41.92, 43.81, 42.25, 40.68, 41.22, 41.49 and 39.38 MB/s. Compared with the preceding optimized build, mean throughput improved from 40.32 to 41.50 MB/s (2.9%) and median throughput improved from 40.05 to 41.49 MB/s (3.6%).

During a controlled transfer, neither node added `UdpInErrors`, `UdpRcvbufErrors` or `UdpSndbufErrors`. The source client occupied roughly 59-85% of one CPU core during the transfer window; the download client occupied roughly 78-104% of one core.

Latency and MTU checks also passed:

- 30 ICMP echoes: 0% loss, 0.594-0.990 ms, mean 0.792 ms;
- 10 echoes carrying a 1400-byte IPv4 packet: 0% loss, 0.490-0.858 ms, mean 0.743 ms.

### Verification and Remaining Scope

- Core tests: 48/48 passed on Windows, including both AES-GCM and HMAC-only multi-hop UDP integration.
- Regression coverage includes route expiry, immediate cache invalidation, zero-allocation throttled cleanup, receive-buffer slices, 32-packet TUN batch lifetime, mesh metadata rejection, and in-place authentication/tampering.
- Full Release solution build passed for the core's `net8.0` and `net10.0` targets.
- Linux x64 Native AOT publish passed, the deployed binary passed `crypto self-test` with the system AES-GCM backend, and both native Linux clients established direct UDP paths.
- Thirty-six JavaScript files passed `node --check`; temporary benchmark files, the HTTP listener, and the temporary baseline firewall rule were removed after testing.
- Windows x64 Native AOT client publish passed; its published executable was not exercised against a real TUN device in this pass.
- Outer `UDP_SEGMENT`/`UDP_GRO`, multi-queue TUN, and native crypto-wrapper changes remain separate follow-up experiments.

Baseline provenance still needs clarification: the first four AES transfer runs have a median of 38.03 MB/s and a best of 39.65 MB/s, rather than the 38.67/40.07 figures used in the first follow-up comparison. Keep the recorded tables intact until the underlying run groups are identified.

## Subsequent Gap Investigation

The 2026-11-02 native Linux investigation measured actual syscall batch sizes, TUN write aggregation, physical path selection, kernel-cycle distribution, CPU steal, and transmit-side loss. See [Linux throughput gap investigation](2026-11-02-linux-throughput-gap-investigation.md). Its diagnostic `sendfile` workload and instrumented runs are separate from the release throughput tables above.

The same investigation document records the subsequent uncommitted outer `UDP_SEGMENT`/`UDP_GRO` implementation and its initial Linux x64 WSL socket validation. It had not yet been deployed at that local-validation checkpoint. The later native follow-up below is separate from the unchanged 41.50 MB/s release benchmark.

## Native Outer UDP Offload Follow-up

The subsequent [2026-11-02 native A/B report](2026-11-02-linux-udp-offload-ab-report.md) completes that deployment and comparison. With fixed private candidates and the same new binary, application GSO/GRO improved the forward mean from 44.12 to 49.64 MB/s. Enabling private-interface UDP segmentation during a bounded trial produced a nine-run forward mean of 58.73 MB/s and reverse mean of 56.80 MB/s. The NIC-enabled setting was restored at the end of that A/B. The later [stability and rollback rollout](2026-11-02-linux-offload-stability-rollout.md) enabled reversible private-NIC persistence on both nodes and records the current operating state and remaining automatic-routing/saturation limits.

Those diagnostic `sendfile` results have different workload/path controls from the release benchmark above and must not silently replace its 41.50 MB/s figure. The new report records reverse TCP retransmissions and the remaining platform/latency boundaries.
