#!/usr/bin/env bash
# Start the isolated local SQL Server, the API on 5080, and Vite on 5173.
# Safe to run again: services that are already listening are left alone.
set -euo pipefail

export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/local/bin:${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

root="$(pwd)"
if [[ ! -f "${root}/XtraPOS.sln" ]]; then
  echo "Run this script from the XtraPOS repository root." >&2
  exit 1
fi

secrets="/etc/xtrapos/dev-sql.env"
if [[ ! -f "${secrets}" ]]; then
  echo "Local SQL environment file is missing. Refusing to start the API." >&2
  exit 1
fi

database="$(awk -F= '/^SqlServer__Database=/{print $2}' "${secrets}" | tr -d "'\"")"
if [[ "${database}" != "XtraPOS_Branch_Test_Cloud" ]]; then
  echo "Refusing to start against a database other than the isolated cloud test database." >&2
  exit 1
fi

port_open() {
  python3 - "$1" <<'PY'
import socket, sys
sock = socket.socket()
sock.settimeout(0.4)
try:
    sock.connect(("127.0.0.1", int(sys.argv[1])))
except Exception:
    sys.exit(1)
finally:
    sock.close()
PY
}

tmux_bin=(tmux)
if [[ -f /exec-daemon/tmux.portal.conf ]]; then
  tmux_bin=(tmux -f /exec-daemon/tmux.portal.conf)
fi

ensure_session() {
  local name="$1"
  local port="$2"
  local command="$3"
  if port_open "${port}"; then
    return 0
  fi
  if "${tmux_bin[@]}" has-session -t "${name}" 2>/dev/null; then
    "${tmux_bin[@]}" kill-session -t "${name}"
  fi
  "${tmux_bin[@]}" new-session -d -s "${name}" -- bash -lc "${command}"
}

if [[ -x /opt/mssql/bin/sqlservr ]]; then
  ensure_session sqlserver 1433 "exec sudo -u mssql /opt/mssql/bin/sqlservr"
  ready=0
  for _ in $(seq 1 90); do
    if port_open 1433; then
      ready=1
      break
    fi
    sleep 1
  done
  if [[ "${ready}" != "1" ]]; then
    echo "Local SQL Server did not listen on 127.0.0.1:1433." >&2
    exit 1
  fi
fi

sudo mkdir -p /usr/share/XtraPOS/data-protection-keys
sudo chown -R ubuntu:ubuntu /usr/share/XtraPOS

ensure_session xtrapos_api 5080 "set -a; source ${secrets}; set +a; export DOTNET_ROOT=/usr/share/dotnet; export PATH=/usr/local/bin:\${DOTNET_ROOT}:\${PATH}; export DOTNET_CLI_TELEMETRY_OPTOUT=1; cd ${root}/src/XtraPOS.Api; exec dotnet run --launch-profile http"
ensure_session xtrapos_web 5173 "export PATH=/usr/local/bin:\${PATH}; cd ${root}/src/XtraPOS.Web; exec npm run dev -- --host 0.0.0.0 --port 5173 --strictPort"

for _ in $(seq 1 90); do
  if port_open 5080 && port_open 5173; then
    break
  fi
  sleep 1
done

curl -fsS http://127.0.0.1:5080/health >/dev/null
curl -fsS http://127.0.0.1:5173/health >/dev/null
