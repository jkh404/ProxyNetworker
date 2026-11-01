#!/usr/bin/env bash
set -eu

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
APP="$SCRIPT_DIR/ProxyNetworker.Client"
PID_DIR="$SCRIPT_DIR/run"
LOG_DIR="$SCRIPT_DIR/logs"
PID_FILE="$PID_DIR/client.pid"
LATEST_LOG="$LOG_DIR/client-console.latest.log"

usage() {
    cat <<'EOF'
Usage: bash run.sh <start|stop|restart|status|logs> [ProxyNetworker.Client arguments...]

Examples:
  bash run.sh start
  bash run.sh start connect --server http://127.0.0.1:13301 --token vnet_xxx
  bash run.sh status
  bash run.sh logs
  bash run.sh stop

Linux virtual-network mode normally needs root or CAP_NET_ADMIN:
  sudo bash run.sh start virtual-network --server SERVER:13301 --token vnet_xxx
EOF
}

read_pid() {
    if [ ! -f "$PID_FILE" ]; then
        return 1
    fi

    local pid
    pid="$(cat "$PID_FILE" 2>/dev/null || true)"
    case "$pid" in
        ''|*[!0-9]*)
            return 1
            ;;
    esac

    printf '%s' "$pid"
}

is_running() {
    local pid
    pid="$(read_pid)" || return 1
    kill -0 "$pid" 2>/dev/null
}

show_connection_summary() {
    local log_file="${1:-$LATEST_LOG}"
    [ -e "$log_file" ] || return 0

    local ready_line server_line vnet_line
    ready_line="$(grep -a 'Remote port tunnel ready' "$log_file" 2>/dev/null | tail -n 1 || true)"
    if [ -z "$ready_line" ]; then
        vnet_line="$(grep -a 'Virtual Network token accepted' "$log_file" 2>/dev/null | tail -n 1 || true)"
        if [ -n "$vnet_line" ]; then
            local network assigned server
            network="$(printf '%s\n' "$vnet_line" | sed -n 's/.*network=\([^,]*\),.*/\1/p')"
            assigned="$(printf '%s\n' "$vnet_line" | sed -n 's/.*assigned=\([^,]*\),.*/\1/p')"
            server="$(printf '%s\n' "$vnet_line" | sed -n 's/.*server=\([^ ]*\).*/\1/p')"
            server="${server%.}"
            printf 'Virtual Network: %s\n' "${network:-unknown}"
            printf 'Assigned address: %s\n' "${assigned:-unknown}"
            printf 'Server endpoint: %s\n' "${server:-unknown}"
            local path_line path_type path_peer path_endpoint path_rtt
            path_line="$(grep -a 'Virtual Network .* path selected' "$log_file" 2>/dev/null | tail -n 1 || true)"
            if [ -n "$path_line" ]; then
                path_type="$(printf '%s\n' "$path_line" | sed -n 's/.*Virtual Network \(direct\|mesh\|relay\) path selected.*/\1/p')"
                path_peer="$(printf '%s\n' "$path_line" | sed -n 's/.* selected for \([^ ]*\) via.*/\1/p')"
                path_endpoint="$(printf '%s\n' "$path_line" | sed -n 's/.* via \([^ ]*\) (.*/\1/p')"
                path_rtt="$(printf '%s\n' "$path_line" | sed -n 's/.* (\([^)]*\)).*/\1/p')"
                printf 'Path: %s %s -> %s (%s)\n' "${path_type:-relay}" "${path_peer:-unknown}" "${path_endpoint:-unknown}" "${path_rtt:-pending}"
            else
                printf 'Path: relay\n'
            fi
        fi
        return 0
    fi

    local name protocol public_port private_target server_authority server_host
    name="$(printf '%s\n' "$ready_line" | sed -n 's/.*name=\([^,]*\),.*/\1/p')"
    protocol="$(printf '%s\n' "$ready_line" | sed -n 's/.*protocol=\([^,]*\),.*/\1/p')"
    public_port="$(printf '%s\n' "$ready_line" | sed -n 's/.*public=:\([0-9][0-9]*\),.*/\1/p')"
    private_target="$(printf '%s\n' "$ready_line" | sed -n 's/.*private=\([^ ]*\).*/\1/p')"
    private_target="${private_target%.}"

    server_line="$(grep -a 'connecting to ' "$log_file" 2>/dev/null | tail -n 1 || true)"
    server_authority="${server_line#*connecting to }"
    server_authority="${server_authority#*://}"
    server_authority="${server_authority%%/*}"
    server_host="${server_authority%:*}"

    printf 'Tunnel: %s (%s)\n' "${name:-unknown}" "${protocol:-unknown}"
    printf 'Assigned public port: %s\n' "${public_port:-unknown}"
    if [ -n "$server_host" ] && [ -n "$public_port" ]; then
        printf 'Public endpoint: %s:%s\n' "$server_host" "$public_port"
    fi
    printf 'Private target: %s\n' "${private_target:-unknown}"
}

start_client() {
    if is_running; then
        echo "ProxyNetworker.Client is already running. pid=$(read_pid)"
        echo "log=$(readlink -f "$LATEST_LOG" 2>/dev/null || true)"
        return 0
    fi

    if ! command -v nohup >/dev/null 2>&1; then
        echo "nohup was not found. Install coreutils or start the client in the foreground." >&2
        return 1
    fi

    if [ ! -x "$APP" ]; then
        chmod +x "$APP" 2>/dev/null || true
    fi

    if [ ! -x "$APP" ]; then
        echo "Client executable is missing or not executable: $APP" >&2
        return 1
    fi

    mkdir -p "$PID_DIR" "$LOG_DIR"
    umask 077

    local timestamp log_file pid
    timestamp="$(date '+%Y%m%d-%H%M%S')"
    log_file="$LOG_DIR/client-console-$timestamp.log"

    nohup "$APP" "$@" >>"$log_file" 2>&1 &
    pid=$!
    printf '%s\n' "$pid" >"$PID_FILE"
    ln -sfn "$(basename -- "$log_file")" "$LATEST_LOG"

    sleep 1
    if ! kill -0 "$pid" 2>/dev/null; then
        rm -f "$PID_FILE"
        echo "ProxyNetworker.Client exited during startup. See log: $log_file" >&2
        tail -n 40 "$log_file" >&2 || true
        return 1
    fi

    local attempt=0
    while ! grep -aEq 'Remote port tunnel ready|Virtual Network token accepted' "$log_file" 2>/dev/null && [ "$attempt" -lt 8 ]; do
        sleep 1
        attempt=$((attempt + 1))
    done

    echo "ProxyNetworker.Client started. pid=$pid"
    echo "log=$log_file"
    show_connection_summary "$log_file"
}

stop_client() {
    if ! is_running; then
        rm -f "$PID_FILE"
        echo "ProxyNetworker.Client is not running."
        return 0
    fi

    local pid
    pid="$(read_pid)"
    kill -TERM "$pid" 2>/dev/null || true

    local attempt=0
    while kill -0 "$pid" 2>/dev/null && [ "$attempt" -lt 10 ]; do
        sleep 1
        attempt=$((attempt + 1))
    done

    if kill -0 "$pid" 2>/dev/null; then
        echo "Client did not stop after 10 seconds; sending SIGKILL. pid=$pid" >&2
        kill -KILL "$pid" 2>/dev/null || true
    fi

    rm -f "$PID_FILE"
    echo "ProxyNetworker.Client stopped. pid=$pid"
}

show_status() {
    if is_running; then
        echo "ProxyNetworker.Client is running. pid=$(read_pid)"
        if [ -L "$LATEST_LOG" ] || [ -f "$LATEST_LOG" ]; then
            echo "log=$(readlink -f "$LATEST_LOG" 2>/dev/null || printf '%s' "$LATEST_LOG")"
            show_connection_summary "$LATEST_LOG"
        fi
        return 0
    fi

    echo "ProxyNetworker.Client is not running."
    return 1
}

follow_logs() {
    local log_file="$LATEST_LOG"
    if [ ! -e "$log_file" ]; then
        log_file="$(ls -1t "$LOG_DIR"/client-console-*.log 2>/dev/null | head -n 1 || true)"
    fi

    if [ -z "$log_file" ] || [ ! -e "$log_file" ]; then
        echo "No console log was found under $LOG_DIR" >&2
        return 1
    fi

    echo "Following $log_file"
    tail -n 100 -f "$log_file"
}

command_name="${1:-start}"
if [ "$#" -gt 0 ]; then
    shift
fi

case "$command_name" in
    start)
        start_client "$@"
        ;;
    stop)
        stop_client
        ;;
    restart)
        stop_client
        start_client "$@"
        ;;
    status)
        show_status
        ;;
    logs|log|tail)
        follow_logs
        ;;
    help|-h|--help)
        usage
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac
