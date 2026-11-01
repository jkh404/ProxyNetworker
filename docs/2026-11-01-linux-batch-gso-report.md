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
