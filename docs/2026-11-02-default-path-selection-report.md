# Default Direct-Path Selection

Date: 2026-11-02, Asia/Shanghai.

Public-report privacy note: node labels, addresses, client ports, process IDs,
and host paths are anonymized or omitted. Example paths below are not deployment
instructions for the original hosts. Measurements and validation outcomes are
unchanged; raw deployment evidence and recovery state remain outside Git.

## Status

The three approved uploads completed and their remote hashes matched. Node B
built the uploaded Core/test sources in a fresh directory and passed **158/158
native Linux tests, with no skips**. Both Linux x64 nodes now run the new Native
AOT executable, using their original configuration and automatic candidates.
No test address allowlist or application-offload-disable environment overrides
remain.

The nine steady-state automatic forward files averaged **58.75 MB/s**, or
**93.8%** of this iteration's 62.64 MB/s ordinary private-TCP baseline. The nine
reverse files averaged **56.59 MB/s**. Actual forwarding logs and interface byte
counters, not candidate-selection messages alone, were retained.

The [previous stability rollout](2026-11-02-linux-offload-stability-rollout.md)
measured 58.73 MB/s on a fixed private path and 51.58 MB/s in its final three
automatic-route files. The new forward steady-state mean is 13.9% above that
earlier automatic sample, with a different sample size and measurement time.
The important result is reproducing the accepted private-path performance
without restricting the default candidate set, not a higher peak.

This is not a fixed speed from the moment of startup: the first file after the
normal restart measured 40.36 MB/s while relay discovery converged to private
direct forwarding. Reverse TCP retransmissions and an ACK-side public-path
transition also remain; see the separately retained cohorts below.

Candidate SHA-256:
`76955f43ca9e3083db8b934b4f5670f4dae587a18a7d66d5a3efa7b10bccc77f`

Local executable:
`publish/ProxyNetworker.Client_stable-direct_20261102_linux-x64_aot/ProxyNetworker.Client`

Core/test source bundle SHA-256:
`0540f7655e4ab5453d167f5ddaaf80cc69a13209015ab3b3ba5e10715b1e8c07`

The bundle contains only `.cs` and `.csproj` files under Core and Core.Tests,
including the new tests. It contains no node configuration, credentials, or
deployment state.

Deployment and rollback workspaces:

- Node A: `/var/tmp/proxynetworker/default-path-node-a/`
- Node B: `/var/tmp/proxynetworker/default-path-node-b/`

Upload metadata is retained locally in `artifacts/default-path/upload-state.json`.
All three existing transfers succeeded; no duplicate uploads were submitted.

## Default Policy

The new policy applies automatically without a candidate allowlist, hard-coded
test addresses, or edits to an existing `appsettings.json`.

- A fresh, authenticated, bidirectionally probed private IPv4 candidate gets a
  bounded 5 ms selection-score preference. Only RFC1918 ranges receive this
  locality hint; loopback, link-local, CGNAT, public, and IPv6 addresses do not.
- Switching away from a still-healthy current path requires an adjusted-score
  improvement of at least the larger of 2 ms and 20% of its raw score.
- A newly proven private candidate can replace public startup fallback without
  waiting for that extra switching margin, provided it wins the bounded
  comparison. A clearly worse private path does not displace a better public path.
- Missing, removed, expired, or explicitly suspended current paths do not receive
  a hysteresis hold. Other proven direct candidates remain eligible; configured
  relay fallback is preserved when no direct candidate is eligible.

This is a bounded locality/performance heuristic, not a bandwidth measurement or
a claim of a globally optimal route on every network. A private address alone
does not prove a faster interface, hardware offload, or an end-to-end capacity.
Real-node tests must establish the result for the target topology.

## Probe Health

The old counters included freshly sent, still-pending probes in the loss penalty.
The new bounded per-path tracker distinguishes pending, acknowledged, and expired
probes. Only matching timestamp/nonce acknowledgements update measurements;
unknown, duplicate, and expired acknowledgements do not refresh proof.

Completed outcomes drive a recent probe-loss estimate: a failure moves the
estimate 25% toward full loss, while a success halves the previous estimate.
This avoids retaining a recovered path's old outage penalty for minutes. The
estimate is not a measured data-packet loss probability.

Sending/enqueuing local traffic no longer refreshes `LastSuccess`. Selection also
requires a fresh probe acknowledgement within the existing `PeerTimeout`
(20 seconds by default), even if authenticated inbound traffic is still arriving.
Thus an asymmetric outbound failure cannot be hidden by continued sending or
receiving on that candidate.

These liveness and probe-accounting fixes remain enabled when the ranking policy
is disabled; a rollback switch must not deliberately restore false liveness or
in-flight-as-loss behavior.

## Forwarding Observability

Candidate bookkeeping now uses the same direct-path ranking as forwarding.
Candidate logs are labeled `candidate selected`, while actual forwarding logs
retain `path selected`. Forwarding signatures include the endpoint, not just the
mesh virtual-address route, so public/private endpoint changes on the same route
are no longer suppressed. The relay-only forwarding branch also records its
actual selection.

Wire framing, encryption/authentication, MTU, candidate advertisement format,
configured relay behavior, and NIC-management settings have not been changed.

## Configuration and Rollback

Core option: `VirtualNetworkTunnelOptions.EnableStableDirectPathSelection`,
default `true`.

The client source-generated configuration loader accepts:

```json
{
  "ProxyNetworkerClient": {
    "virtualNetwork": {
      "stableDirectPathSelection": false
    }
  }
}
```

This is a field illustration, **not a replacement for the existing node
configuration**. Preserve all existing fields and credentials. Omission defaults
to `true`; explicit `false` restores raw-score ranking without the private
preference or switching hysteresis. Both direct-config and token-config startup
propagate the setting. Restart through the existing client wrapper after editing
the field; this is not a hot-reloaded local setting. The generated sample includes
the enabled default, and normal startup still reads configuration without
command-line overrides.

Before activation, the running executable and original configuration/environment
were backed up in each new node-specific workspace. The original executable hash,
verified both before deployment and during an actual restore drill, is
`3c1656a4049c5db0367e9ad709b6d8efdcde753ee1a1f91b62a477eeb19e63c3`.
The earlier A/B workspaces and their older binary backups were not overwritten
or re-prepared. Each new workspace retains `original-client`,
`original-appsettings.json`, and `state.json`; the latter includes the original
process environment and must be treated as sensitive deployment state.

Independent 45-minute systemd restore timers protected the attended activation.
Both original binaries were actually restored and a complete 256 MiB transfer
succeeded. Both candidates were then reactivated and verified. The timers were
stopped after final acceptance, so they cannot unexpectedly revert this deployment.

Attended binary rollback for Node A:

```sh
python3 /var/tmp/proxynetworker/offload-ab-node-a/Control-LinuxOffloadTest.py \
  restore --workspace /var/tmp/proxynetworker/default-path-node-a \
  --client-directory /opt/proxynetworker-client
```

For Node B:

```sh
python3 /var/tmp/proxynetworker/offload-ab-node-b/Control-LinuxOffloadTest.py \
  restore --workspace /var/tmp/proxynetworker/default-path-node-b \
  --client-directory /opt/proxynetworker-client
```

The controller checks executable identity and configuration hashes. It refuses
to silently overwrite later configuration changes; reconcile those separately
before an attended restore. `activate --mode both` with the same workspace
reactivates the retained candidate without an allowlist.

The independent private-NIC service and its existing original-state journal
remain in place. Binary/configuration rollback and NIC-state rollback are
separate operations; neither requires disabling AES-GCM.

## Local Verification

- Windows core tests: 143 passed, 6 Linux-only test methods skipped.
- The new policy, probe history, and tunnel-selection tests add 44 passing cases.
- Full Release solution builds for its .NET 8/.NET 10 targets: zero warnings and
  zero errors.
- Linux x64 Native AOT publication passed, using the existing temporary
  PublishAotClang injection pattern without changing project package metadata.
- A managed verification harness exercised the actual source-generated client
  loader: omitted field -> true, explicit false -> false, explicit true -> true.
- Core/test bundle archive contents were inspected and scanned for embedded
  token/private-key literals; no deployment configuration is included.

Coverage includes bounded private preference, clearly faster public paths,
startup promotion, small-score stability, explicit raw-ranking mode, in-flight
probes, nonce matching, expiry, bounded memory, loss-estimate recovery, stale proof
despite outbound/inbound traffic, actual probe-loss fallback and recovery,
immediate cache invalidation, configured relay fallback, explicit cooldown, and
actual forwarding endpoint logs.

## Real-Node Throughput

Measurements ran on 2026-11-02, approximately 09:50-09:57 UTC
(17:50-17:57 Asia/Shanghai), with AES-GCM, MTU 1400, application GSO/GRO, and the
already-managed private-NIC offload enabled. The workload is the existing
256 MiB sparse-file/sendfile HTTP diagnostic. Rates below are curl's decimal
MB/s per complete file, not the helper's lower launcher-inclusive aggregate.
The ordinary-TCP baseline used private addresses directly; all virtual-network
cohorts used original configuration and unrestricted automatic candidates.

| Cohort | Files | Mean MB/s | Median | Min | Max | Final TCP retransmissions |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Ordinary private TCP, before/after | 6 | 62.64 | 62.65 | 62.58 | 62.66 | 0 |
| Automatic steady state, A -> B | 9 | 58.75 | 58.75 | 58.72 | 58.77 | 0 |
| Automatic steady state, B -> A | 9 | 56.59 | 56.58 | 55.25 | 57.63 | 1,885 |
| Automatic forward after private recovery | 3 | 58.77 | 58.77 | 58.77 | 58.78 | 0 |
| First three forward files after restart | 3 | 52.63 | 58.77 | 40.36 | 58.77 | 1 |
| Old-binary restore drill, first forward file | 1 | 34.11 | 34.11 | 34.11 | 34.11 | 2 |

All **31 file downloads** completed and their final sender snapshots had
`unacked=0` and `notsent_bytes=0`. The cold-start and rollback samples are not
silently excluded from the evidence or included in the nine-file steady-state
means. Cohorts were defined by their test phase, not selected afterward by rate
or interface.

Forward steady-state file rates, in measurement order:
`58.769321, 58.767301, 58.723551, 58.742429, 58.750066, 58.753795,
58.721740, 58.741426, 58.770195`.

Reverse rates:
`55.254040, 57.631853, 56.575115, 56.689048, 56.568426, 57.223271,
56.564302, 56.089927, 56.679221`.

Forward sender `eth1` transmitted 2,620,164,857 bytes across the nine files;
`eth0` transmitted only 87,980 bytes. Reverse sender `eth1` transmitted
2,643,763,686 bytes and `eth0` 18,787 bytes. Thus both bulk-data directions used
private egress. However, Node A's actual forwarding log changed from private
to public at 09:51:26 UTC during reverse load: its ACK/small-packet direction
was not permanently pinned to private. This policy deliberately permits a
sufficiently better proven public candidate to win.

The reverse sender recorded 1,885 retransmissions across 1,794,135 data segments
out, about 0.105%. This is a sender-counter ratio, not measured outer-UDP loss.
It is not a zero-retransmission acceptance result. The earlier heavy four-flow
and duplex saturation tests were not repeated with this candidate.

No measured transfer window added host-wide `UdpInErrors`, `UdpRcvbufErrors`,
or `UdpSndbufErrors`, observed interface drops, or qdisc drops. This statement
applies to this iteration's retained windows, not the previous rollout, which
did contain UDP send-buffer errors in an application-off fallback test.

## Discovery and Recovery

After both old binaries had been restored, one complete rollback-drill transfer
measured 34.11 MB/s on public egress. This establishes that rollback works, not
the old version's steady-state capacity.

Both new binaries were then reactivated. The first new forward file was started
immediately after the normal wrapper reported Node B started; no warm-up sleep
or candidate allowlist was inserted. Its rate was **40.359976 MB/s**, with
141,965,313 source public-interface TX bytes and 152,248,048 private-interface TX
bytes. Both clients logged actual relay forwarding at 09:55:52 UTC. Node B
logged private direct forwarding at 09:55:56 and Node A at 09:55:57. The next
two complete files measured **58.770697 and 58.770440 MB/s** on private egress.

This is real relay data fallback during discovery, followed by automatic direct
promotion. It does not claim that a five-second discovery interval disappears
or that the first cold file has steady-state throughput.

An asymmetric fault was confined to Node A's outgoing private UDP client flow:
`10.20.0.11:45001 -> 10.20.0.12:45002` on `eth1`. No interface was disabled,
and SSH, the management endpoints, other peers, and server UDP port 41039 were
not blocked. An independent 70-second cleanup timer bounded the fault even if
the foreground supervisor failed. The attended supervisor removed its rules
after 45.040 seconds.

- A simultaneous Node B -> Node A ping stream kept traffic arriving.
  The exact inbound-client-flow counter observed 83 packets before the switch;
  inbound traffic was not merely assumed.
- Node A logged actual public forwarding **17.212 seconds** after injection.
  The last valid probe preceded injection, so this is compatible with the
  existing 20-second proof lifetime, not a new 17-second timeout guarantee.
- The supervisor observed private forwarding again at **82.490 seconds**, or
  **37.450 seconds after the block was removed**. Probe-loss decay and fresh
  acknowledgements govern recovery; it is not instantaneous.
- The independent inbound ping completed 650 probes with 568 replies. The
  82 missing replies during this intentional fault are retained, not described
  as zero loss. Three complete forward files after recovery averaged 58.77 MB/s.

The first fault supervisor exited nonzero while stopping an already-fired,
unloaded transient cleanup timer. Its fault rules had already been removed.
An independent audit confirmed no remaining injection rules or live ping
processes and recovered all 218 timed snapshots through 109.816 seconds.
The supervisor's own final outbound-ping summary was not saved and is not
claimed as a completed measurement. The script's final status must not be
misreported as a clean pass; the forwarding and independent cleanup evidence
are recorded separately.

No live fault simultaneously blocked both public and private direct candidates.
Configured relay forwarding was exercised during cold discovery, while
no-direct-path relay selection and recovery also passed the native core tests.

## Final Audit

- Both running executable hashes match the candidate above; process identities
  were checked against each node's PID file.
- Original `appsettings.json` bytes match their backups. Both clients run bare,
  from `/opt/proxynetworker-client`, with no configuration arguments or
  test environment overrides. Startup logs confirm stable selection is `True`.
- Node A retains `10.77.0.11`; Node B retains `10.77.0.12`; MTUs remain 1400.
- Both AES-GCM self-tests pass using the system backend. Both configured
  management health endpoints are healthy with `quicSupported:true`; this is
  not a QUIC throughput measurement.
- Both private `eth1` offloads remain active/requested on; both public `eth0`
  offloads remain off. The NIC service remains enabled and active/exited.
  Its original-state journal still records off/off and was not recaptured.
- Final ordinary pings returned 30/30 in both directions, means 0.779 and
  0.746 ms. DF inner-IPv4 1400-byte pings (`-M do -s 1372`) returned 10/10
  in both directions, means 0.747 and 0.685 ms.
- No matching error/fatal, malformed-frame, or data-loop/offload-failure messages
  were found in the checked latest candidate logs.
- Temporary port 31883 listeners and helpers have exited. All four temporary
  HTTP access rules and the fault rules are absent. Both deployment restore
  timers and the fault cleanup timer are inactive. No managed commands remain
  active.
- No persistent firewall, route, sysctl, qdisc, public-NIC, server, or credential
  changes were made. Binary rollback did not disable the independent NIC service.
- No commit or push, whole-host reboot, Linux ARM64 data-plane run, or Windows
  TUN data-plane run was performed.

Raw evidence is retained locally under `artifacts/default-path/`:
per-node raw-evidence JSON files, `summary.json`,
`native-core-tests.log`, `deployment-events.json`,
`fault-supervisor-output.json`, and `final-pings.json`. The raw node evidence
contains every transfer record, relevant candidate/actual-forwarding logs,
final audits, completed ping logs, and the asymmetric fault snapshots.
Original JSONL files, Linux test results, and rollback backups remain in the
node-specific workspaces above.
