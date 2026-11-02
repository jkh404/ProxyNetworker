# Native Linux UDP Offload A/B

Date: 2026-11-02, Asia/Shanghai.

Public-report privacy note: node labels, addresses, and host paths are anonymized
examples. Measurements and validation outcomes are unchanged; raw deployment
evidence and recovery state are not included in Git.

## Result

The outer UDP GSO/GRO implementation is effective, but the complete fast path also depends on the selected interface's UDP segmentation feature.

- With the private NIC feature left off, enabling application GSO/GRO increased forward mean throughput from 44.12 to 49.64 MB/s, or 12.5%.
- With application GSO/GRO and private NIC UDP segmentation enabled, nine forward runs averaged 58.73 MB/s and nine reverse runs averaged 56.80 MB/s.
- The six ordinary private-TCP baseline runs averaged 62.65 MB/s. The forward NIC-enabled result was approximately 93.7% of that baseline.
- Four simultaneous forward connections achieved 58.38 MB/s aggregate without new UDP buffer errors or qdisc drops.

These are controlled diagnostic results, not a revision of the earlier 41.50 MB/s release benchmark. The diagnostic workload uses a sparse file and `sendfile`; this comparison also fixes private candidates.

**Final state:** both nodes retain the new AOT client with application GSO/GRO enabled. The temporary candidate allowlist was removed, both private NIC UDP segmentation features were restored to their original **off** state, and no boot-time configuration was added. Consequently, 58.73 MB/s is a tested configuration, not a claim about the final default configuration.

**Later deployment:** that paragraph records the end of this A/B experiment.
The subsequent [stability and rollback rollout](2026-11-02-linux-offload-stability-rollout.md)
enabled reversible private-NIC boot persistence on both hosts and reproduced
58.73 MB/s forward. It also documents automatic public/private path switching and
remaining reverse saturation retransmissions; the current operating state and
rollback commands are in that follow-up report.

## Identity and Method

| Item | Value |
| --- | --- |
| Source candidate SHA-256 | `3c1656a4049c5db0367e9ad709b6d8efdcde753ee1a1f91b62a477eeb19e63c3` |
| Previous deployed client SHA-256 | `33d675b29eeb0ae1dce10f304f310dc59f731b48c4375d363b9d8f74878a55d6` |
| Node A | One vCPU; private `10.20.0.11`; virtual `10.77.0.11` |
| Node B | Two vCPUs; private `10.20.0.12`; virtual `10.77.0.12` |
| Kernel and architecture | Linux `6.8.0-147-generic`, x64 |
| Private interface | `eth1`, `virtio_net`, one combined queue |
| Data plane | Direct UDP, AES-GCM, MTU 1400 |
| Workload | 256 MiB HTTP/1.1 download, fresh connection per run, decimal MB/s |

Both nodes kept their original configuration bytes, tokens, virtual addresses, authentication mode, and MTU. `recvmmsg`/`sendmmsg`, TUN virtio metadata, and TUN GSO remained enabled.

For the controlled runs, `PN_DIRECT_ADDRESS_ALLOWLIST=10.20.0.11,10.20.0.12` restricted advertised and accepted direct candidates. Configured relay endpoints remained available. Interface counters confirmed bulk traffic used the private interface; small public-interface control/management traffic remained.

Application offload was toggled using `PN_DISABLE_UDP_GSO` and `PN_DISABLE_UDP_GRO` with the same binary. The main software comparison used off -> both -> both -> off blocks, three forward and three reverse files per block. NIC UDP segmentation was off throughout that comparison.

The NIC experiments changed only `tx-udp-segmentation` on the private interface. Foreground supervisors restored the original feature state on completion, signal, or deadline. No qdisc, sysctl, route policy, firewall rule, server binary, or persistent network configuration was modified.

## Application Offload

| Direction | Mode | Runs | Mean MB/s | Median | Min | Max |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| A -> B | Both off | 6 | 44.12 | 44.51 | 42.60 | 45.22 |
| A -> B | Both on | 6 | 49.64 | 49.61 | 48.98 | 50.29 |
| B -> A | Both off | 6 | 47.87 | 48.63 | 43.18 | 50.00 |
| B -> A | Both on | 6 | 48.94 | 49.03 | 48.13 | 49.67 |

Forward mean improved by 12.5% and median by approximately 11.4%. Reverse mean improved by approximately 2.2%, but median improved by only approximately 0.8%; these runs do not establish a substantial reverse throughput gain from application flags alone.

Two-run diagnostic splits:

| Direction | GSO only MB/s | GRO only MB/s |
| --- | --- | --- |
| A -> B | 48.02, 48.75 | 44.70, 45.21 |
| B -> A | 47.47, 47.54 | 52.66, 51.43 |

The splits are exploratory, not another interleaved six-run comparison. They support a predominantly transmit-side forward benefit, but do not establish an optimal global setting for every host or direction.

## NIC Segmentation

Both private interfaces initially reported:

```text
tx-udp-segmentation: off
```

The feature was configurable. A valid two-run Node A transmit-only trial produced 58.73 and 58.78 MB/s. A subsequent nine-run confirmation enabled the feature on both private interfaces:

| Direction | Runs | Mean MB/s | Median | Min | Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| A -> B | 9 | 58.73 | 58.74 | 58.54 | 58.78 |
| B -> A | 9 | 56.80 | 56.92 | 55.67 | 57.66 |

Forward runs: 58.739, 58.744, 58.734, 58.717, 58.779, 58.773, 58.541, 58.778, 58.776 MB/s.

Reverse runs: 57.659, 56.919, 57.362, 55.666, 56.834, 56.950, 56.407, 56.442, 56.933 MB/s.

The private-TCP baseline was 62.636/62.642/62.656 MB/s before the comparison and 62.665/62.651/62.648 MB/s afterwards. A reverse private-TCP baseline was not collected because the existing Node B firewall does not admit that temporary private listener. The forward LAN ratio must not be presented as a measured reverse LAN ratio.

### CPU and Guest Interface Counters

Means per forward 256 MiB file; process percentages are relative to one logical CPU:

| Configuration | Source process CPU | Receiver process CPU | Source private-interface TX packets/skbs |
| --- | ---: | ---: | ---: |
| Application offloads off; NIC feature off | 63.75% | 94.23% | 199155 |
| Application offloads on; NIC feature off | 46.05% | 103.26% | 199154 |
| Application offloads on; NIC feature on | 34.52% | 35.81% | 8658 |

These are guest processing counters, not wire-frame counts. The result is consistent with retaining larger aggregates through the virtual interface path, but the cloud host implementation and physical hardware execution were not inspected. Interface byte counters also change their header accounting with offload; they cannot establish an exact wire overhead or theoretical bandwidth ceiling.

The reverse NIC-enabled runs had a larger mean transmit-message count, approximately 69,166 UDP messages per file, versus approximately 8,673 in the forward direction. Given approximately 199,000 original TCP data segments per file, this points to much smaller effective reverse send groups. The queue drains immediately after its first wakeup; concurrent producer/consumer scheduling on the two-vCPU sender is a candidate explanation, not a demonstrated causal result.

## Independent Syscall Samples

One instrumented forward file per software mode, NIC feature off. These samples are excluded from throughput statistics:

| Counter | Application offloads off | Application offloads on |
| --- | ---: | ---: |
| Submitted original frames | 199320 | 199321 |
| Submitted native UDP messages/groups | 199320 | 9423 |
| Successful `sendmmsg` calls | 8250 | 9322 |
| GSO groups | 0 | 9156 |
| Original frames in GSO groups | 0 | 199054 |
| Receiver original frames | 199171 | 199168 |
| Receiver native messages | 199171 | 174604 |
| Successful `recvmmsg` calls | 70895 | 68549 |
| Receiver GRO messages | 0 | 18988 |
| Original frames in GRO messages | 0 | 43552 |
| Receiver TUN writes | 76821 | 75991 |

GSO reduced UDP message processing units substantially; it did not reduce the number of send syscalls in this sample. Receive aggregation and TUN write count changed much less. This is why merely increasing the configured syscall batch size does not explain or solve the gap.

The samples include background control traffic. `bpftrace` 0.20.2 emitted address-space/signedness warnings for the bounded user-memory walks; counts were sanity-checked against complete file transfers and host counters. These samples do not partition CPU cycles or measure exact per-flow loss.

Early sampling windows that missed the transfer were excluded. An initial NIC trial also expired before its two downloads began; `nic-on-forward-*` records are explicitly excluded from NIC results. The valid trial and confirmation windows are retained separately with task timestamps and feature-state checks.

## Correctness and Remaining Risk

- Native Node B core tests: 114/114 passed, no skips.
- Windows core tests: 99 passed, 6 Linux-only test methods skipped.
- Release solution and Windows/Linux x64 Native AOT publication passed. Both staged Linux clients passed AES-GCM self-test using the system backend.
- Four simultaneous forward files achieved 58.375 MB/s aggregate. All four files completed; per-flow rates must not be summed as an aggregate because completion times differ.
- Controlled software A/B, NIC confirmation, and the four-flow group added no UDP buffer errors or qdisc drops.
- Ten DF echoes with a full 1400-byte inner IPv4 packet had zero loss, average 0.733 ms.
- After restoring normal candidates and the original NIC feature state, 30 ordinary echoes had zero loss, average 0.762 ms.
- The nine NIC-enabled forward files recorded 3 host-wide TCP retransmissions; the reverse files recorded 2012, with out-of-order receive counters also increasing. The four-flow group recorded one retransmission. Absence of UDP buffer errors is not absence of network loss or TCP recovery.

Reverse retransmissions and sender grouping need further controlled investigation before treating the NIC setting as a universally safe default. Preserve the TUN GSO producer burst when testing send-queue changes; do not blindly delay ACK/control frames or overwrite live zero-copy receive views.

Windows real TUN traffic, Linux ARM64, public-interface NIC offload, and loaded interactive latency were not tested in this pass.

## Deployment and Reproduction

Both deployed clients now match the candidate hash above. Original executables, configuration backups, and guarded restoration state remain in:

- Node A: `/var/tmp/proxynetworker/offload-ab-node-a/`
- Node B: `/var/tmp/proxynetworker/offload-ab-node-b/`

The current clients use their original configuration, unrestricted automatic candidates, application GSO/GRO enabled, and private NIC UDP segmentation restored off. Both management health endpoints remain healthy with `quicSupported:true`; this is not QUIC throughput validation.

Temporary HTTP listeners on port 31883, sparse files, tracing processes, and NIC supervisors have exited. No firewall rules were added or removed. Backups and diagnostic evidence were intentionally retained.

To reproduce the NIC-enabled condition on these validated hosts, identify the actual output interface first, inspect support, then explicitly enable it for an attended test:

```sh
ethtool --json -k eth1
ethtool -K eth1 tx-udp-segmentation on
# Run the fixed-private-candidate comparison.
ethtool -K eth1 tx-udp-segmentation off
```

These commands require appropriate administrative privileges. Do not copy the `eth1` name to another machine without checking its routes and interface identity. Persistent deployment and public-path behavior are separate work: no boot-time offload configuration was installed in this pass.

Local evidence: `artifacts/udp-offload/private-ab/`, including complete raw result files, summary JSON, task timing, native test output, syscall trace logs, and the final audit. Remote `ab-results.jsonl` files remain the original per-node records. `scripts/Diagnose-LinuxTransfer.py`, `scripts/Control-LinuxOffloadTest.py`, and `scripts/Trace-LinuxUdpOffload.bt` provide the diagnostic and controlled-restart tooling.
