import argparse
import concurrent.futures
import http.server
import json
import os
import re
import resource
import signal
import socket
import statistics
import struct
import subprocess
import tempfile
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path


SIZE = 256 * 1024 * 1024
HZ = os.sysconf("SC_CLK_TCK")
TCP_KEYS = {"RetransSegs", "InSegs", "OutSegs"}
IP_KEYS = {"InDiscards", "OutDiscards", "FragCreates", "FragFails", "ReasmFails"}
EXT_KEYS = {
    "TCPTimeouts", "TCPSynRetrans", "TCPOFOQueue", "TCPOFODrop",
    "TCPRcvCoalesce", "TCPOrigDataSent", "TCPLostRetransmit",
}


def process_stats(pid):
    root = Path("/proc") / str(pid)
    text = (root / "stat").read_text()
    fields = text[text.rindex(")") + 2:].split()
    result = {"user": int(fields[11]), "system": int(fields[12])}
    result["threads"] = {}
    for task in (root / "task").iterdir():
        try:
            text = (task / "stat").read_text()
            fields = text[text.rindex(")") + 2:].split()
            result["threads"][task.name] = {
                "name": (task / "comm").read_text().strip(),
                "user": int(fields[11]), "system": int(fields[12]),
            }
        except FileNotFoundError:
            continue
    return result


def counters(path, groups):
    lines = Path(path).read_text().splitlines()
    result = {}
    for index in range(0, len(lines), 2):
        header, values = lines[index].split(), lines[index + 1].split()
        group = header[0].rstrip(":")
        if group not in groups:
            continue
        wanted = groups[group]
        result[group] = {
            key: int(value) for key, value in zip(header[1:], values[1:])
            if wanted is None or key in wanted
        }
    return result


def snapshot(pid, tun):
    names = {"eth0", "eth1", tun}
    result = {
        "time": time.monotonic(),
        "client": process_stats(pid),
        "helper": process_stats(os.getpid()),
        "cpu": [int(value) for value in Path("/proc/stat").read_text().splitlines()[0].split()[1:9]],
        "interfaces": {},
        "snmp": counters("/proc/net/snmp", {"Udp": None, "Tcp": TCP_KEYS, "Ip": IP_KEYS}),
        "netstat": counters("/proc/net/netstat", {"TcpExt": EXT_KEYS}),
        "qdiscs": json.loads(subprocess.check_output(["tc", "-s", "-j", "qdisc", "show"], text=True)),
    }
    for name in names:
        root = Path("/sys/class/net") / name / "statistics"
        result["interfaces"][name] = {
            key: int((root / key).read_text())
            for key in ("rx_bytes", "tx_bytes", "rx_packets", "tx_packets", "rx_dropped", "tx_dropped")
        }
    return result


def current_pid(args):
    return int(Path(args.pid_file).read_text().strip()) if args.pid_file else args.pid


def delta(before, after):
    elapsed = after["time"] - before["time"]
    result = {"seconds": round(elapsed, 4)}
    for group in ("client", "helper"):
        previous, current = before[group], after[group]
        result[group] = {
            name + "_pct": round(100 * (current[name] - previous[name]) / HZ / elapsed, 2)
            for name in ("user", "system")
        }
        result[group]["threads"] = {}
        for tid, task in current["threads"].items():
            old = previous["threads"].get(tid, {"user": 0, "system": 0})
            ticks = task["user"] + task["system"] - old["user"] - old["system"]
            if ticks:
                result[group]["threads"][tid] = {
                    "name": task["name"], "cpu_pct": round(100 * ticks / HZ / elapsed, 2),
                }
    cpu = [value - old for value, old in zip(after["cpu"], before["cpu"])]
    total = sum(cpu)
    result["host_cpu"] = {
        name: round(100 * value / total, 2)
        for name, value in zip(("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal"), cpu)
    }
    for group in ("interfaces", "snmp", "netstat"):
        result[group] = {
            name: {key: value - before[group][name][key] for key, value in values.items()}
            for name, values in after[group].items()
        }
    previous_qdiscs = {(item["dev"], item.get("handle")): item for item in before["qdiscs"]}
    result["qdiscs"] = []
    for item in after["qdiscs"]:
        old = previous_qdiscs.get((item["dev"], item.get("handle")))
        if old is not None:
            result["qdiscs"].append({
                "dev": item["dev"], "kind": item["kind"],
                **{name: item.get(name, 0) - old.get(name, 0)
                   for name in ("bytes", "packets", "drops", "overlimits", "requeues")},
            })
    return result


def tcp_info(connection):
    return connection.getsockopt(socket.IPPROTO_TCP, socket.TCP_INFO, 256).hex()


def decode_tcp_info(value):
    data = bytes.fromhex(value)
    fields = {
        "unacked": 24, "lost": 32, "rtt_us": 68, "rttvar_us": 72,
        "snd_cwnd": 80, "total_retrans": 100, "segs_out": 136,
        "segs_in": 140, "notsent_bytes": 144, "data_segs_out": 156,
        "dsack_dups": 216, "reord_seen": 220, "rcv_ooopack": 224,
    }
    return {
        name: struct.unpack_from("=I", data, offset)[0]
        for name, offset in fields.items() if len(data) >= offset + 4
    }


def ping_summary(output):
    times = [float(value) for value in re.findall(r"time[=<]([\d.]+)\s*ms", output)]
    counts = re.search(r"(\d+) packets transmitted, (\d+) received", output)
    result = {
        "transmitted": int(counts[1]) if counts else None,
        "received": int(counts[2]) if counts else None,
        "samples": len(times),
    }
    if times:
        ordered = sorted(times)
        result.update({
            "min_ms": min(times), "median_ms": statistics.median(times),
            "p95_ms": ordered[max(0, (95 * len(ordered) + 99) // 100 - 1)],
            "max_ms": max(times), "mean_ms": statistics.mean(times),
        })
    return result


def serve(args):
    stop = threading.Event()
    completed = {}
    completed_lock = threading.Lock()
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    signal.signal(signal.SIGINT, lambda *_: stop.set())
    with tempfile.TemporaryFile(dir="/tmp") as data:
        data.truncate(SIZE)

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *_):
                pass

            def send_json(self, value):
                body = json.dumps(value).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(body)
                self.close_connection = True

            def do_POST(self):
                if self.client_address[0] not in args.allow or self.path != "/stop":
                    self.send_error(403)
                    return
                self.send_json({"stopping": True})
                stop.set()

            def do_GET(self):
                if self.client_address[0] not in args.allow:
                    self.send_error(403)
                    return
                request = urllib.parse.urlparse(self.path)
                if request.path == "/snapshot":
                    value = snapshot(current_pid(args), args.tun)
                    with completed_lock:
                        value["completed_tcp"] = dict(completed)
                    self.send_json(value)
                    return
                if request.path != "/file":
                    self.send_error(404)
                    return
                key = urllib.parse.parse_qs(request.query).get("run", ["unknown"])[0]
                self.send_response(200)
                self.send_header("Content-Length", str(SIZE))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.flush()
                offset = 0
                while offset < SIZE:
                    sent = os.sendfile(self.connection.fileno(), data.fileno(), offset, SIZE - offset)
                    if sent <= 0:
                        raise RuntimeError("sendfile made no progress")
                    offset += sent
                deadline = time.monotonic() + 2
                while True:
                    info = tcp_info(self.connection)
                    decoded = decode_tcp_info(info)
                    if (decoded.get("unacked", 0) == 0 and decoded.get("notsent_bytes", 0) == 0
                            or time.monotonic() >= deadline):
                        break
                    time.sleep(0.002)
                with completed_lock:
                    completed[key] = info
                self.close_connection = True

        servers = [http.server.ThreadingHTTPServer((address, args.port), Handler) for address in args.bind]
        threads = [threading.Thread(target=server.serve_forever, daemon=True) for server in servers]
        for thread in threads:
            thread.start()
        print(json.dumps({"ready": args.bind, "port": args.port, "helper_pid": os.getpid()}), flush=True)
        try:
            stop.wait(args.duration)
        finally:
            for server in servers:
                server.shutdown()
                server.server_close()
            print(json.dumps({"stopped": True}), flush=True)


def fetch_json(url, method="GET"):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    request = urllib.request.Request(url, method=method)
    with opener.open(request, timeout=10) as response:
        return json.load(response)


def run(args):
    control = "http://" + args.control + ":" + str(args.port)
    remote_before = fetch_json(control + "/snapshot")
    local_before = snapshot(current_pid(args), args.tun)
    usage_before = resource.getrusage(resource.RUSAGE_CHILDREN)
    ping = None
    if args.ping:
        ping = subprocess.Popen(
            ["ping", "-n", "-i", "0.05", "-W", "1", args.server],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            env={**os.environ, "LC_ALL": "C"},
        )
    start = time.monotonic()

    def transfer(index):
        run_id = args.label + "-" + str(index)
        url = "http://" + args.server + ":" + str(args.port) + "/file?run=" + run_id
        result = subprocess.run(
            ["curl", "--noproxy", "*", "-sS", "--max-time", "60", "-o", "/dev/null", "-w", "%{json}", url],
            capture_output=True, text=True, check=True,
        )
        value = json.loads(result.stdout)
        if int(value["size_download"]) != SIZE:
            raise RuntimeError("incomplete transfer")
        payload_seconds = value["time_total"] - value["time_starttransfer"]
        return {
            "id": run_id, "bytes": SIZE, "seconds": value["time_total"],
            "connect_seconds": value["time_connect"], "first_byte_seconds": value["time_starttransfer"],
            "MBps": value["speed_download"] / 1_000_000,
            "payload_MBps": SIZE / payload_seconds / 1_000_000,
        }

    ping_result = None
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.flows) as pool:
            transfers = list(pool.map(transfer, range(args.flows)))
    finally:
        elapsed = time.monotonic() - start
        if ping is not None:
            ping.send_signal(signal.SIGINT)
            try:
                output, _ = ping.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                ping.kill()
                output, _ = ping.communicate()
            ping_result = ping_summary(output)
    usage_after = resource.getrusage(resource.RUSAGE_CHILDREN)
    local_after = snapshot(current_pid(args), args.tun)
    remote_after = fetch_json(control + "/snapshot")
    deadline = time.monotonic() + 3
    while any(value["id"] not in remote_after["completed_tcp"] for value in transfers):
        if time.monotonic() >= deadline:
            raise RuntimeError("Sender TCP completion statistics are missing")
        time.sleep(0.02)
        remote_after = fetch_json(control + "/snapshot")
    print(json.dumps({
        "label": args.label, "flows": args.flows, "transfers": transfers,
        "aggregate_MBps": round(sum(value["bytes"] for value in transfers) / elapsed / 1_000_000, 3),
        "curl_cpu_seconds": round(
            usage_after.ru_utime + usage_after.ru_stime - usage_before.ru_utime - usage_before.ru_stime, 4),
        "source": delta(remote_before, remote_after), "download": delta(local_before, local_after),
        "tcp_info": {value["id"]: remote_after["completed_tcp"].get(value["id"]) for value in transfers},
        "tcp_decoded": {
            value["id"]: decode_tcp_info(remote_after["completed_tcp"][value["id"]])
            for value in transfers
        },
        "loaded_ping": ping_result,
    }), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("serve", "run", "stop"))
    parser.add_argument("--pid", type=int)
    parser.add_argument("--pid-file")
    parser.add_argument("--tun")
    parser.add_argument("--bind", action="append")
    parser.add_argument("--allow", action="append")
    parser.add_argument("--server")
    parser.add_argument("--control")
    parser.add_argument("--port", type=int, default=31883)
    parser.add_argument("--duration", type=int, default=600)
    parser.add_argument("--flows", type=int, default=1)
    parser.add_argument("--label", default="diagnostic")
    parser.add_argument("--ping", action="store_true")
    args = parser.parse_args()
    if args.mode == "serve":
        serve(args)
    elif args.mode == "run":
        run(args)
    else:
        print(json.dumps(fetch_json("http://" + args.control + ":" + str(args.port) + "/stop", "POST")))


if __name__ == "__main__":
    main()
