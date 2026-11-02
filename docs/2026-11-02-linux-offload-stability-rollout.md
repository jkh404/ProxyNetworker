# Linux UDP Offload Stability and Rollback

Date: 2026-11-02, Asia/Shanghai.

Public-report privacy note: node labels, addresses, process IDs, and host paths
are anonymized or omitted. Paths are illustrative; measurements and validation
outcomes are unchanged. Raw deployment evidence is not included in Git.

## Status

The accepted performance target is the already measured forward 58.73 MB/s,
approximately 93.7% of the 62.65 MB/s private-network baseline. This iteration
prioritizes stability, observability, and reversible deployment, not a higher
peak speed.

Both nodes now have the opt-in `proxynetworker-udp-offload.service` installed,
active, and enabled at boot. It enables only private `eth1` UDP segmentation and
retains the original off/off active/requested values for restoration. Public
`eth0` remains unchanged and off. Normal automatic candidates and application
GSO/GRO are restored; no test allowlist or offload-disable flags remain.

The fixed-private confirmation reproduced **58.73 MB/s forward** and **56.81 MB/s
reverse**. The six baseline files averaged 62.63 MB/s; the forward ratio is
approximately 93.8%, consistent with the earlier approximately 93.7% result.
This is not a new speed target or a meaningful increase over the
[previous A/B report](2026-11-02-linux-udp-offload-ab-report.md).

**Important boundary:** automatic routing can still choose the unmodified public
interface. The final three automatic-route files averaged **51.58 MB/s**, ranging
from **46.29 to 58.79 MB/s**. Persistent private-NIC enablement therefore makes the
fast path available; it does not guarantee 58.73 MB/s for every automatic path.
Heavy reverse concurrency still produces TCP retransmissions, including on a
fixed private path. This deployment is a reversible, user-requested opt-in on
these two hosts, not blanket acceptance of arbitrary load or other interfaces.

## Validation Results

The client binary is unchanged from the A/B candidate:
`3c1656a4049c5db0367e9ad709b6d8efdcde753ee1a1f91b62a477eeb19e63c3`.
Both retain AES-GCM, MTU 1400, original virtual IPv4 addresses and configuration
bytes. These remain 256 MiB sparse-file/sendfile HTTP diagnostics, in decimal
MB/s, rather than a replacement for the older release workload.

| Cohort | Files | Mean MB/s | Min | Max | Final per-flow TCP retransmissions |
| --- | ---: | ---: | ---: | ---: | ---: |
| Ordinary private TCP, before/after | 6 | 62.63 | 62.62 | 62.65 | 0 |
| Fixed private, NIC on, A -> B | 3 | 58.73 | 58.68 | 58.76 | 0 |
| Fixed private, NIC on, B -> A | 3 | 56.81 | 56.56 | 57.15 | 692 |
| Final automatic configuration, A -> B | 3 | 51.58 | 46.29 | 58.79 | 224 |

All these final sender snapshots had `unacked=0` and `notsent_bytes=0`. The fixed
forward confirmation's 273/273 concurrent ICMP probes returned; individual-run
median RTTs were 4.49-4.51 ms and p95 RTTs were 5.56-5.63 ms. The fixed reverse
confirmation returned 280/280 probes, with individual-run medians 4.67-5.98 ms.
Its 692 retransmissions across 598,108 data segments out correspond to a 0.116%
sender counter ratio, not a measured UDP loss probability.

There are 53 retained transfer records covering 65 complete file downloads.
This includes automatic-route off/on blocks, matched private-path stress tests,
application fallback, persistent-setting rollback, and final configuration
checks. Mixed/public-path records are retained, not silently discarded.

## Automatic Routing

Interface byte counters and endpoint-selection logs confirm that some automatic
runs use public `eth0` or switch between public and private paths. For example,
the final three source interface TX deltas are retained with their complete
records in `artifacts/udp-offload/stability/node37.raw-results.json`.

The public/private transitions coincide with lower throughput and some DSACK
and reordering events. A private NIC switch cannot accelerate an outer packet
sent through the public NIC. This does not explain every reverse retransmission:
the fixed-private stress results below reproduce retransmissions without path
switching. Neither public-interface offload nor a routing-policy change was
silently introduced to hide this distinction.

## Saturation Risk

One matched fixed-private trial per NIC mode, application GSO/GRO enabled:

| Reverse four-connection group | Aggregate MB/s | Flow retransmissions | Retrans/data-segments-out ratio | ICMP replies | Ping p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| NIC off | 49.47 | 15,212 | 1.87% | 118/120 | 46.5 ms |
| NIC on | 57.08 | 29,031 | 3.52% | 120/120 | 30.6 ms |

All eight file downloads completed, and final TCP queues drained. Turning the
NIC feature on improved throughput and sampled latency here, but the higher
reverse retransmission ratio is a remaining issue, not a passed zero-loss test.
One trial per mode cannot establish a general causal loss rate.

The matched concurrent bidirectional trial improved the overlapping forward
flow rate from 25.79 to 38.62 MB/s and reverse flow rate from 46.86 to 57.39 MB/s.
Reverse TCP retransmissions were 4,497 with the NIC off and 1,787 with it on;
all 60 probes per direction/mode returned. These are individual overlapping-flow
rates, not a sum advertised as a simultaneous aggregate capacity.

The matched pressure samples used fixed-count ping processes that were allowed
to finish, avoiding an unacknowledged final probe when interrupting ping.
They sample the first part of the transfer, not every instant of its duration.
Earlier automatic-route `--ping` records retain their actual interrupted-process
counts, including missing replies; those are not relabeled as zero loss.

No matched pressure window added UDP buffer errors, qdisc drops, or observed
TUN-interface drops. That does not prove absence of loss elsewhere in the cloud
path. The separate application-off fallback reverse window added 34 host-wide
`UdpSndbufErrors`; the transfer still completed. Therefore the full iteration
must not be described as having zero UDP errors.

## Retransmission Follow-up

The previous diagnostic retained sender `TCP_INFO` bytes for each connection.
Decoding the Linux UAPI `tcpi_total_retrans` and `tcpi_data_segs_out` fields gives:

| Reverse configuration | Runs | Flow retransmissions | Flow data segments out | Ratio |
| --- | ---: | ---: | ---: | ---: |
| Application offloads off; NIC feature off | 6 | 704 | 1,183,232 | 0.0595% |
| Application offloads on; NIC feature off | 6 | 1,296 | 1,183,804 | 0.1095% |
| Application offloads on; NIC feature on | 9 | 1,903 | 1,774,711 | 0.1072% |

These are sender snapshot counters, not a measured UDP packet-loss percentage.
The old snapshots were taken after `sendfile` submission, while some tail
segments were still unacknowledged, so they are not final, fully acknowledged
flow totals. The previous 2,012 figure is the broader host counter delta, not the
sum of these individual connection snapshots.

Retransmissions already existed without NIC segmentation. The data do not show
that enabling the NIC feature introduced the reverse retransmissions, nor do
they identify the root cause. The similar application-on ratios with the NIC
feature off/on are useful evidence, not proof of equivalence or long-term
reliability.

`scripts/Diagnose-LinuxTransfer.py` now waits up to two seconds for the sender's
unacknowledged and not-yet-sent queues to empty before recording TCP statistics.
Decoded fields still include `unacked` and `notsent_bytes`, so an expired wait is
visible. The receiver waits for those statistics before returning a result.
Optional `--ping` measures concurrent ICMP latency and transmitted/received counts
under load. Throughput timing ends at transfer completion, before statistics
collection.

## Managed NIC Setting

`scripts/Manage-LinuxUdpOffload.py` creates a dedicated, opt-in systemd oneshot
service. It does not replace the client startup command or modify
`appsettings.json`, credentials, AES-GCM, MTU, routes, firewall, sysctl, or qdisc.

Installation requires an explicitly named interface and an RFC1918 peer address.
It verifies the peer route, interface MAC/driver, configurable UDP segmentation,
and active checksum support. Existing installation or recovery files are never
overwritten.

Files owned by this installation:

| Path | Purpose |
| --- | --- |
| `/etc/proxynetworker/udp-offload.json` | Explicit interface and private peer |
| `/var/lib/proxynetworker/udp-offload-state.json` | Original feature state and ownership journal |
| `/usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py` | Administrative helper |
| `/etc/systemd/system/proxynetworker-udp-offload.service` | Opt-in persistence and stop-time restoration |

Installation captures the original state but does not enable the service or
change the NIC. Activation journals ownership before invoking `ethtool`. A
partially failed activation attempts to restore the original state. A failed
restoration retains ownership and reports an error, permitting an explicit retry.
Repeated starts never recapture or overwrite the original state. Interfaces that
were already enabled by somebody else are not claimed or disabled.

At service start, missing capabilities or a changed private route fall back to
normal software networking. Changed interface identity is never modified. The
service has no mandatory dependency from the client, so an unavailable optional
optimization does not prevent the client from starting.

This is not a continuous loss-detection watchdog: a successful feature activation
does not prove the absence of a later silent driver/path problem. Runtime
capability errors are handled by the existing application fallback; unexplained
loss, latency, or driver regressions require an attended rollback.

## Deployment and Rollback

The following installation and activation steps were completed on both nodes.
The stop/restore/re-enable sequence was also exercised, rather than only
documented. On another host, verify identity and actual private egress first;
do not rerun installation over the existing recovery files.

Install once from the uploaded script on each node:

```sh
# Node A; use peer 10.20.0.11 instead on Node B.
sudo python3 Manage-LinuxUdpOffload.py install --interface eth1 --peer-address 10.20.0.12
sudo systemd-analyze verify /etc/systemd/system/proxynetworker-udp-offload.service
```

For an attended trial, start without enabling boot persistence:

```sh
sudo systemctl start proxynetworker-udp-offload.service
sudo python3 /usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py status
```

After stability checks and a real start/stop recovery drill:

```sh
sudo systemctl enable --now proxynetworker-udp-offload.service
```

Layer 1, persistent NIC rollback without restarting the client:

```sh
sudo systemctl disable --now proxynetworker-udp-offload.service
sudo python3 /usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py status
```

`ExecStop` restores the captured original feature values, rather than assuming
that every host originally had the feature off. If the service is already failed
or inactive, retry the journaled restoration directly:

```sh
sudo python3 /usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py restore
```

Layer 2, application rollback for an attended investigation: set
`PN_DISABLE_UDP_GSO=1` and `PN_DISABLE_UDP_GRO=1` in the client's actual service
environment and restart through its existing wrapper. Remove them to recover the
application fast path. These flags do not disable AES-GCM, ordinary UDP batching,
or TUN GSO. They are emergency/diagnostic overrides, not a replacement for normal
application configuration.

This application fallback was exercised with both flags set on both nodes:
forward 44.08 MB/s and reverse 47.60 MB/s, complete transfers with their original
Tokens, virtual IPv4 addresses, and AES-GCM. Both flags were subsequently removed.
The persistent NIC rollback was followed by another complete forward transfer at
49.07 MB/s before the service was re-enabled. Neither test replaced or weakened
authentication.

Layer 3, executable rollback: retain and hash-check the previous client and its
original configuration/environment in the node-specific A/B backup directories
listed in the previous report. Do not mix the test controller's binary restore
with unrelated configuration changes. The NIC service is independent of the
executable and must be rolled back separately when restoring a full baseline.

## Final Audit

Completed in this iteration:

- Windows management tests: 17 passed; 3 Linux diagnostic tests skipped.
- Windows core tests: 99 passed; 6 Linux-only test methods skipped.
- Native Linux management/diagnostic tests: 20/20 on each host, no skips.
- Node B native core regression suite: 114/114 passed, no skips.
- Earlier focused native UDP offload/fallback tests: 55/55 passed, including
  capability failure after partial submission without duplicate packet retries.
- Actual persistent-service enable/disable/start/stop and application-off/on
  recovery drills completed; the original NIC state was never recaptured.
- Both clients retain the candidate hash above and start without command-line
  configuration. Both `appsettings.json` hashes match their original backups.
- Node A retains `10.77.0.11`; Node B retains `10.77.0.12`; both MTUs are 1400.
- Both private `eth1` features are active/requested on; both public `eth0`
  features remain off. The manager's original active/requested states remain off.
- Both units are `ActiveState=active`, `SubState=exited`, `UnitFileState=enabled`.
  This is the expected state of the oneshot service.
- Both configured management endpoints return healthy with `quicSupported:true`;
  both standalone AES-GCM self-tests pass using the system backend. This is not
  QUIC throughput validation.
- Final ordinary pings: 30/30 replies in each direction, mean 0.744 and 0.782 ms.
  Final DF 1400-byte inner IPv4 pings: 10/10 each direction, mean 0.734 and
  0.706 ms.
- No matching data-loop failure, malformed/authentication-failure, or offload
  path-failure messages were found in the checked client logs during this pass.
- Temporary port 31883 listeners and HTTP helpers have exited. Both owned
  rollback/reset timers are inactive. No managed command tasks remain active.
- No server, firewall, sysctl, qdisc, persistent route, or public-NIC settings
  were modified. No commit or push was made.

Final client identities were verified against their PID files. Both retained old
executable backups with hash
`33d675b29eeb0ae1dce10f304f310dc59f731b48c4375d363b9d8f74878a55d6`.
The installed manager hash on each host is
`ea4f3d345343bd7febed419fcd2dcaa5b46600690013acf2f090e750e7742a5a`.

Local evidence is under `artifacts/udp-offload/stability/`, including both raw
result files, summary, setup and service events, final audit, final ping output,
and native core-test output. Original per-node `stability-results.jsonl` and the
uploaded scripts are retained in:

- Node A: `/var/tmp/proxynetworker/stability-node-a/`
- Node B: `/var/tmp/proxynetworker/stability-node-b/`

No whole-host reboot, Linux ARM64 traffic, Windows TUN traffic, or public-interface
NIC offload was validated in this iteration. Startup ordering was checked with
`systemd-analyze verify` and actual service restarts; enabled boot persistence
must not be described as a completed whole-host reboot test. Remaining work is
automatic-path stability and reverse saturation behavior, not a claim that the
accepted single-flow private-path result has failed.
