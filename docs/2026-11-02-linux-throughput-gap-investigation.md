# Linux Throughput Gap Investigation

Date: 2026-11-02, Asia/Shanghai.

Public-report privacy note: node labels, addresses, and host paths are anonymized
examples. Measurements and validation outcomes are unchanged; raw deployment
evidence and credentials are not included in Git.

This document records chronological investigation and implementation checkpoints. The latest completed measurements and deployment state are in [native Linux UDP offload A/B](2026-11-02-linux-udp-offload-ab-report.md).

## Scope and Build Identity

This investigation used the already-deployed, uncommitted route-maintenance and mesh-buffer version. No production client was restarted or replaced. No token, virtual address, route, firewall rule, socket-buffer setting, qdisc, NIC offload, or encryption mode was changed.

Both deployed Linux x64 clients had SHA-256:

```text
33d675b29eeb0ae1dce10f304f310dc59f731b48c4375d363b9d8f74878a55d6
```

Both ran kernel `6.8.0-147-generic`, system AES-GCM, MTU 1400, UDP socket batching, and TUN virtio metadata. Node A had one vCPU; Node B had two. Both private NICs used `virtio_net`, one combined queue, TCP TSO/GSO/GRO enabled, and `tx-udp-segmentation: off`. Both client cgroups reported `cpu.max = max 100000`.

The diagnostic HTTP helper bound only private or virtual addresses and allowed only the counterpart node. It used `sendfile` with a sparse 256 MiB temporary file, removing ordinary filesystem/Python copying from the source side. Every download returned `268435456` bytes. These diagnostic rates are not a replacement for the previous nine-run release benchmark.

## Measured Transfer Rates

Fresh HTTP/1.1 connections, no application encryption changes:

| Direction and Path | Instrumentation | Runs | MB/s |
| --- | --- | ---: | --- |
| Node A -> Node B, private TCP | Counter snapshots only | 2 | 62.659, 62.654 |
| Node A -> Node B, virtual TCP/UDP | Counter snapshots only | 2 | 46.353, 43.197 |
| Node B -> Node A, virtual TCP/UDP | Counter snapshots only | 2 | 50.465, 49.143 |
| Node A -> Node B, four virtual connections | System-wide perf sampling | 1 group | 41.279 aggregate |
| Node A -> Node B, virtual TCP/UDP | Syscall tracepoints | 1 | 44.521 |
| Node B -> Node A, virtual TCP/UDP | Syscall tracepoints and mpstat | 2 | 50.012, 48.131 |

The first four runs were interleaved LAN -> virtual -> virtual -> LAN. A reverse private-TCP baseline was not run because that node's existing firewall did not permit the temporary private listener; no rule was added.

The two timed reverse trace runs connected in 0.871/0.848 ms and reached the first byte in 2.697/2.055 ms. Payload-only rates were 50.037/48.149 MB/s. Handshake delay was not material in those two runs; this does not exclude handshake retries in other runs.

The unchanged binary's direction-dependent rates show that the earlier approximately 34% deficit is not a fixed implementation constant. They do not establish a new optimization gain.

## Where the Extra Work Appears

### 1. Offload Stops at the Outer UDP Boundary

For one private TCP run, the source NIC counted 5,772 transmitted host packets/skbs and 268,816,642 bytes. The comparable virtual run counted 199,271 packets across the physical NICs and approximately 299,140,980 bytes. TCP counted approximately 185,405 original data segments on the native path versus 199,160 on the virtual path.

The approximately 34.5x difference is in host packet-processing units after native TCP offload, not in the number of MTU-sized frames on the wire. Native TCP can carry tens of KiB per skb; the tunnel splits its TUN GSO input into separately encrypted UDP datagrams, so UDP/IP, allocation, routing, filtering, conntrack, and queueing work occurs for each datagram.

The guest interface byte counters expanded by approximately 11.3%. Native TCP offload can also change header accounting in those counters, so this is not an exact on-wire overhead measurement and must not be converted directly into a throughput ceiling. The provider's bandwidth-accounting policy was not verified. The packet counters, kernel profile, and batch measurements below establish additional processing costs without relying on that assumption.

The native source received approximately 5,771 ACK packets / 381,619 bytes. The virtual source received approximately 47,000 ACK-related packets / 7.2 MB on the physical interfaces. More frequent ACKs and their own framing add reverse-path kernel work.

### 2. Configured Batch Size Is Not Effective Batch Size

Tracepoints counted positive `sendmmsg`/`recvmmsg` return values, not requested vector lengths:

| Direction | Role | Datagrams | Syscalls | Mean Batch |
| --- | --- | ---: | ---: | ---: |
| Forward | Node A sends data | 199322 | 8509 | 23.42 |
| Forward | Node B receives data | 199298 | 67641 | 2.95 |
| Forward | Node B sends ACK/control | 39761 | 26936 | 1.48 |
| Forward | Node A receives ACK/control | 39735 | 14155 | 2.81 |
| Reverse, two files | Node B sends data | 398427 | 35048 | 11.37 |
| Reverse, two files | Node A receives data | 398366 | 61728 | 6.45 |
| Reverse, two files | Node A sends ACK/control | 20867 | 15549 | 1.34 |
| Reverse, two files | Node B receives ACK/control | 20847 | 13310 | 1.57 |

The tracing windows include small amounts of background control traffic; they are not payload-only packet counters.

In the forward trace, the source had 4,908 successful TUN reads, with 4,068 in the 32-64 KiB bucket. The receiver performed 74,038 TUN writes totaling 273,126,990 bytes, averaging approximately 3.60 KiB per write. Only 115 writes reached the 32-64 KiB bucket.

In the reverse trace, the one-core receiver's average UDP batch was larger and each file produced approximately 37,000-40,000 TUN receive packets instead of the forward direction's approximately 74,000. ACK/control datagrams were approximately 10,400 per file instead of approximately 39,700. This is strong evidence that batching/coalescing efficiency contributes to the directional difference, but changing direction also changes CPU scheduling, so it is not an isolated causal A/B test.

The current receive loop flushes its TUN batch after each `recvmmsg`. Small receive batches therefore bound TUN coalescing opportunities. Coalescing also stops on incompatible TCP headers/options or sequence discontinuities. Reading another batch into the same arrays before flushing would corrupt the retained mesh-buffer views; any larger-batch experiment must preserve buffer ownership.

### 3. Most Sampled Cycles Are in the Kernel

The four-connection run used system-wide `perf record -a -F 199 -g`, approximately 35 seconds per host, with no lost samples:

| Sampled DSO | Node A | Node B |
| --- | ---: | ---: |
| Kernel | 69.11% | 76.84% |
| ProxyNetworker.Client | 15.96% | 10.04% |
| libcrypto.so.3 | 9.68% | 7.28% |
| libc.so.6 | 3.19% | 3.67% |

Visible kernel stacks include `udp_sendmsg`, `__ip_append_data`, skb allocation/copying, route lookup, `nft_do_chain`, syscall entry/exit, scheduler accounting, and virtqueue processing. OpenSSL `ERR_clear_error` was 2.80%/1.78% of total sampled cycles.

These are shares of sampled host cycles, not shares of transfer wall time or a partition of the throughput deficit. They include kernel work outside the client process and the small profiling/helper overhead. Some Native AOT method names were unresolved, so exact managed-method attribution remains incomplete.

The result supports reducing kernel packet-processing units before replacing AES-GCM or writing a custom OpenSSL wrapper.

### 4. Host Scheduling Is a Separate Limit

Node A's observed CPU steal was 18.73%, 24.60%, and 31.36% in the forward single/single/four-connection windows. Node B's corresponding steal was approximately 0-0.1%.

During the timed reverse transfers, `mpstat` independently reported approximately 19-26% steal, 18-25% softirq, and 0-3% idle in active one-second intervals on Node A. Its available single-vCPU capacity was essentially consumed while some time was unavailable to the guest.

No client cgroup CPU quota was configured. These measurements identify guest scheduling pressure but do not identify the provider's underlying CPU policy. More vCPUs or a less contended host need a controlled comparison before being credited with a throughput gain. In particular, the one-core receiver was faster in the reverse direction, so core count alone is not the explanation.

### 5. Higher Load Introduces Transmit-Side Loss

Single-connection diagnostic windows added no UDP receive-buffer errors and no UDP send-buffer errors. Small TCP retransmission deltas were still observed; absence of UDP buffer errors does not mean zero TCP retransmissions.

The four-connection profiled group added 571 `UdpSndbufErrors` and 571 `IpOutDiscards` on Node A, plus 1,039 TCP retransmitted segments. The receiver added no UDP receive-buffer errors.

Both source interfaces used `fq` with `flow_limit 100p`. The private interface's cumulative flow-limit drops increased from 252 before the diagnostic series to 614 afterwards. Its public-interface cumulative drops were also nonzero. This supports inspecting transmit queue pressure; it does not attribute every one of the 571 errors to a particular qdisc because per-interface start/end counters were not captured around that group.

Increasing receive buffers again would not address this transmit-side observation. Four parallel flows did not lift aggregate throughput above the single-flow diagnostic rates.

### 6. "Direct" Does Not Mean "Private-Only"

The current route/probe logs alternated between private and public candidate addresses. Physical-interface counters confirmed that the first forward virtual run sent almost all data on the public interface; the second used approximately 96 MB public and 203 MB private. One later reverse run used almost all private data while ACK/control traffic still used the public interface.

These were direct peer paths, not the public relay. However, the virtual and ordinary private-LAN comparisons did not use identical physical paths. A future causal A/B comparison must pin private candidates on both peers or explicitly stratify results by physical path.

## Next Experiments, in Order

1. Run a private-candidate-only controlled comparison, retaining the current binary, tokens, and virtual addresses where practical. Record CPU steal, connection timing, effective batch sizes, and qdisc deltas for each transfer.
2. Add optional outer `UDP_SEGMENT` and `UDP_GRO` with capability probing and fallback. Keep each frame's existing independent authentication and preserve MTU rules; do not send a single oversized unsegmented datagram.
3. Investigate receive batch/TUN coalescing with bounded latency and distinct owned buffers. Do not overwrite live zero-copy views, loosen TCP compatibility checks blindly, or delay ACKs without measuring interactive latency.
4. Investigate data-socket pacing and transmit queue pressure under several flows. Test narrower per-socket/application controls before changing a shared host qdisc.
5. Compare a less contended CPU allocation after the data path is held constant. Defer crypto-wrapper replacement and multiqueue TUN until the measurements justify them.

## Evidence and Reproduction

Native diagnostics helper: `scripts/Diagnose-LinuxTransfer.py`. It uses a temporary sparse file, `sendfile`, current client/host counters, explicit address bindings and source allowlists, bounded lifetime, and child-process supervision.

Example native Linux commands, replacing runtime PIDs and addresses after discovery:

```sh
python3 scripts/Diagnose-LinuxTransfer.py serve \
  --pid <source-client-pid> --tun <source-tun> \
  --bind <source-private-ip> --bind <source-virtual-ip> \
  --allow <download-private-ip> --allow <download-virtual-ip> \
  --duration 600

python3 scripts/Diagnose-LinuxTransfer.py run \
  --pid <download-client-pid> --tun <download-tun> \
  --control <source-virtual-ip> --server <source-private-ip> \
  --label native-baseline

python3 scripts/Diagnose-LinuxTransfer.py run \
  --pid <download-client-pid> --tun <download-tun> \
  --control <source-virtual-ip> --server <source-virtual-ip> \
  --label virtual
```

Retained remote evidence directories:

- Node A: `/var/tmp/proxynetworker/gap-node-a/`
- Node B: `/var/tmp/proxynetworker/gap-node-b/`

They contain the copied diagnostic helper, `forward-four.perf.data`, `forward-one.bpf.txt`, and `reverse-two.bpf.txt`. Node A additionally contains `reverse-two.jsonl` and `reverse-two.mpstat.txt`. Earlier counter-only run output is retained in the managed SSH task history, not in those JSONL files.

Both temporary HTTP helpers exited, port 31883 was confirmed absent on both hosts, and sparse files were automatically removed. The deployed binary hashes remained unchanged. The management `/health` endpoint remained healthy with `quicSupported:true`; that does not prove QUIC data-plane performance.

The diagnostic runs above did not change the core implementation or deployment. Windows and Linux ARM64 were not tested in those runs.

## Implementation Follow-Up: Outer UDP Offload

Date: 2026-11-02. Source changes remain uncommitted. No production client was replaced or restarted for this follow-up.

### Changes

- The Linux batch transport probes `UDP_SEGMENT` and `UDP_GRO` independently. Existing IPv4, 64-bit little-endian Linux ABI checks remain explicit; unavailable capabilities retain plain `recvmmsg`/`sendmmsg`.
- Contiguous, equal-sized frames to the same endpoint are submitted as one GSO message using scatter/gather vectors, at most 32 original frames and 65,507 payload bytes. There is no new userspace concatenation buffer. Each original frame retains its own nonce and AES-GCM/HMAC tag; wire versions and packet lengths do not change.
- GRO receives use bounded 128 KiB buffers. Ancillary metadata and truncation flags are validated before splitting into offset/length views. Each frame is parsed and authenticated separately, and receive buffers are not reused until the awaited TUN batch flush completes.
- Receive size checks apply to individual segments. Native tests caught a valid short tail being discarded when GRO combined it with earlier oversized segments; the implementation now retains the valid tail without accepting oversized frames.
- Partial `sendmmsg` results are mapped from GSO groups back to original frame counts. Unsupported offload/path errors disable GSO for that socket and retry only unsent frames with plain batching. A native test forces a GSO rejection using `SO_NO_CHECK` on its own loopback test socket, after a preceding ordinary frame has already been submitted.
- `SafeSocketHandle` interop, native send/receive timeouts, receive-side synchronization, and deferred send cleanup prevent descriptor reuse and native-buffer release while a syscall is active.

The transport attempts both offloads by default and logs the enabled flags at startup. These process-level switches are independent:

```sh
PN_DISABLE_UDP_GSO=1 PN_DISABLE_UDP_GRO=1 ./ProxyNetworker.Client
```

Run from the existing client directory so the normal configuration is loaded. Set either variable alone to disable only that capability. They do not disable socket batching, TUN GSO, or authentication. `PN_DISABLE_GSO_TX=1` remains the separate TUN transmit-aggregation switch.

### Verification

Environment: Windows host and a separate Linux x64 WSL2 source copy, Linux kernel `5.15.167.4-microsoft-standard-WSL2`, .NET SDK `10.0.403`, runtime `10.0.14`. The Linux SDK was staged in an isolated user directory, not installed over the existing SDK. These are native Linux socket calls on loopback, not native cloud-host or physical-NIC tests.

| Verification | Result |
| --- | --- |
| Windows core tests | 96 passed, 6 Linux-only test methods skipped |
| Linux x64 core tests | 111/111 passed, no skips |
| Repeated Linux native UDP cases | 15/15 passed |
| Linux mesh integration with both offloads disabled by environment | 3/3 passed |
| Full Release solution, core `net8.0` and `net10.0` | Passed, zero warnings/errors |
| Windows x64 Native AOT publish | Passed |
| Linux x64 Native AOT client publish | Passed, using the existing temporary PublishAotClang injection pattern |
| Published Linux x64 AOT client `crypto self-test` on WSL | Passed, system AES-GCM backend |
| Separate Linux x64 Native AOT UDP socket smoke harness on WSL | 8/8 passed |

The Linux matrix covers all four GSO/GRO combinations in both AES-GCM and HMAC-only modes. A 32-frame test checks successful GSO submissions, actual GRO metadata and nonzero-offset views, payload identity/order, and independent authentication. GRO may leave some frames unaggregated; this is valid and must not be mistaken for packet loss.

Coverage also includes cmsg ABI widths/alignment, malformed/duplicate ancillary data, bounded splitting, oversized packets and short tails, tampering isolation, partial-send recovery without duplicate frames, pooled queue ownership, and disposal with active native receive calls.

Local test evidence is retained under `artifacts/udp-offload/test-results/`. The isolated WSL source and test results are under `/var/tmp/proxynetworker/isolated-linux-validation/`.

The standalone AOT socket harness is under `artifacts/udp-offload/native-smoke/`; it references the current core and exercises all four GSO/GRO combinations in both authentication modes, without touching a TUN device or the deployed clients. Its enabled GSO+GRO cases submitted 32 independently authenticated frames and received all 32; counters confirmed actual GRO processing, which need not cover every frame. This smoke check complements, but does not replace, the managed test suite or real-node data-plane tests.

The staged Linux x64 Native AOT client is `artifacts/udp-offload/linux-x64/ProxyNetworker.Client`, SHA-256:

```text
1f946c4693b3bdc10d2d08994050a145217984f9b02e3af95cb3a1e2a1eda47b
```

### Remaining Measurement

This follow-up establishes behavior, compatibility, and fallback, not an end-to-end throughput gain. The previous 41.50 MB/s release benchmark still describes the previous deployed binary.

The next controlled measurement must use the same staged Linux x64 binary with the two offload flags toggled, fixed private candidates on both peers, MTU 1400, unchanged authentication, and interleaved runs. Capture CPU steal, effective batch sizes, GRO activity, TUN write sizes, and qdisc/UDP error deltas. Successful GSO submissions do not by themselves prove hardware offload or absence of queue drops.

Neither cloud-node throughput, Linux ARM64, nor Windows real TUN traffic was retested for this implementation. No production routes, firewall rules, sysctls, tokens, or virtual addresses were changed.

## Native Host Staging

The remote tool connection was reinitialized on 2026-11-02. Read-only inspection confirmed both connected test nodes still ran the original `33d675b2...78a55d6` client, with virtual addresses `10.77.0.11` on Node A and `10.77.0.12` on Node B. Their observed private addresses remained `10.20.0.11` and `10.20.0.12`, respectively. Logs still alternated between private and public direct candidates.

At the user's request, the previously verified Linux x64 AOT client was uploaded first, without replacing either running client:

| Node | Isolated staging directory |
| --- | --- |
| A | `/var/tmp/proxynetworker/offload-ab-node-a/` |
| B | `/var/tmp/proxynetworker/offload-ab-node-b/` |

Both `ProxyNetworker.Client.initial` files matched SHA-256 `1f946c4693b3bdc10d2d08994050a145217984f9b02e3af95cb3a1e2a1eda47b`. Both passed `crypto self-test` on the actual native Linux nodes, using the system `AesGcm` backend. Both copies of `Diagnose-LinuxTransfer.py` matched SHA-256 `bd4fcbcf771d17876ffe153323ac1fa7e11d6ed9c4fc6adbf39641b75140f132`.

The source also adds an opt-in direct candidate IPv4 allowlist, exposed to the client as `PN_DIRECT_ADDRESS_ALLOWLIST`. It filters local advertisements and accepted remote candidates while retaining the configured relay. The default remains unrestricted. Regression tests cover default behavior, local/remote filtering, relay preservation, and IPv6 rejection.

The corresponding private-path A/B client was published to `artifacts/udp-offload/private-ab/linux-x64/`, SHA-256 `3c1656a4049c5db0367e9ad709b6d8efdcde753ee1a1f91b62a477eeb19e63c3`. Windows core tests passed 99 cases with 6 Linux-only test methods skipped, and the full Release solution built with zero warnings/errors.

The diagnostic helper now reads the active PID file after each restart and captures qdisc deltas. `scripts/Control-LinuxOffloadTest.py` stages backups, checks executable/configuration identity, and supports controlled activation and restoration without editing the client configuration.

At the earlier upload checkpoint, the second batch (private-path candidate and control script for both hosts, plus core test source for Node B) was still in the tool's `Approval` phase. No client had been restarted at that point. Those prerequisites and measurements were subsequently completed as recorded below.

## Completed Native Follow-up

The upload checkpoint above was subsequently cleared. Native Node B tests passed 114/114, both cloud clients were backed up and updated, and the controlled private-path software/NIC comparisons are now complete. See [native Linux UDP offload A/B](2026-11-02-linux-udp-offload-ab-report.md) for the full data, excluded windows, retransmission caveats, and final deployment state.

The principal measured forward results are 44.12 MB/s with application offloads disabled, 49.64 MB/s with them enabled and the NIC feature still off, and 58.73 MB/s across nine files with the private NIC UDP segmentation feature enabled. At the end of that A/B, temporary allowlists, NIC feature changes, and diagnostic services were removed or restored. The earlier unmeasured/undeployed statements describe their earlier checkpoints.

The subsequent [stability and rollback rollout](2026-11-02-linux-offload-stability-rollout.md) enabled reversible private-interface boot persistence on both hosts, exercised actual NIC/application recovery, and reproduced 58.73 MB/s forward. It separates automatic public/private path switching from fixed-path results and retains the higher reverse-concurrency retransmission risk. That follow-up records the current operating state; these chronological checkpoints do not supersede it.
