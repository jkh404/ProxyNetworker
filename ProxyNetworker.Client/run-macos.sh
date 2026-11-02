#!/usr/bin/env bash
set -eu
export LC_ALL=C
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
APP="$SCRIPT_DIR/ProxyNetworker.Client"
STATE_DIR="$HOME/Library/Application Support/ProxyNetworker"
PID_FILE="$STATE_DIR/client.pid"
LOG_FILE="$STATE_DIR/logs/client-console.log"

read_pid() {
    [ -f "$PID_FILE" ] || return 1
    PID="$(sed -n '1p' "$PID_FILE")"
    case "$PID" in ''|*[!0-9]*) return 1 ;; esac
}

is_running() {
    read_pid || return 1
    kill -0 "$PID" 2>/dev/null || return 1
    local recorded actual
    recorded="$(sed -n '2p' "$PID_FILE")"
    actual="$(ps -p "$PID" -o lstart= 2>/dev/null || true)"
    [ -n "$recorded" ] && [ "$recorded" = "$actual" ]
}

start_client() {
    if is_running; then
        printf 'ProxyNetworker.Client is already running. pid=%s\n' "$PID"
        return
    fi
    [ -x "$APP" ] || { printf 'Client is missing or not executable: %s\n' "$APP" >&2; exit 1; }
    mkdir -p "$STATE_DIR/logs"
    cd -- "$SCRIPT_DIR"
    nohup "$APP" "$@" >>"$LOG_FILE" 2>&1 &
    local pid="$!" started
    started="$(ps -p "$pid" -o lstart= 2>/dev/null || true)"
    sleep 1
    if [ -z "$started" ] || ! kill -0 "$pid" 2>/dev/null; then
        printf 'Client exited during startup. Check %s\n' "$LOG_FILE" >&2
        return 1
    fi
    printf '%s\n%s\n' "$pid" "$started" >"$PID_FILE"
    printf 'ProxyNetworker.Client started. pid=%s\nlog=%s\n' "$pid" "$LOG_FILE"
}

stop_client() {
    if ! is_running; then
        printf 'No matching client process is running.\n'
        return
    fi
    local pid="$PID" attempts=0
    kill -INT "$pid"
    while is_running && [ "$attempts" -lt 50 ]; do
        sleep 0.2
        attempts=$((attempts + 1))
    done
    if is_running; then
        printf 'Client did not stop in 10 seconds; terminating the recorded process.\n' >&2
        kill -KILL "$pid"
    fi
    rm -f -- "$PID_FILE"
    printf 'ProxyNetworker.Client stopped.\n'
}

command_name="${1:-start}"
if [ "$#" -gt 0 ]; then shift; fi
case "$command_name" in
    start) start_client "$@" ;;
    stop) stop_client ;;
    restart) stop_client; start_client "$@" ;;
    status)
        if is_running; then
            printf 'ProxyNetworker.Client is running. pid=%s\nlog=%s\n' "$PID" "$LOG_FILE"
        else
            printf 'No matching client process is running.\n'
            exit 1
        fi
        ;;
    logs) [ -f "$LOG_FILE" ] && tail -n 100 -f "$LOG_FILE" ;;
    *) printf 'Usage: bash run.sh [start|stop|restart|status|logs]\n' >&2; exit 2 ;;
esac
