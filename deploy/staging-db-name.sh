#!/usr/bin/env bash
# Print only SqlServer__Database for xtrapos-staging.service.
# Install as /usr/local/sbin/xtrapos-staging-db-name and allow xtrapos-deploy
# to run that exact path with sudo -n. This file does not print any other setting.
# It accepts no arguments, so it cannot be pointed at another unit or file.
set -euo pipefail

[[ "$#" -eq 0 ]] || {
  echo "DB_VERIFIER_ARGS" >&2
  exit 2
}

service="xtrapos-staging.service"
systemctl_bin="systemctl"
if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${SYSTEMCTL_BIN:-}" && -x "${SYSTEMCTL_BIN}" ]]; then
  case "$SYSTEMCTL_BIN" in
    /tmp/*|/var/tmp/*) ;;
    *) echo "DB_VERIFIER_BIN" >&2; exit 2 ;;
  esac
  systemctl_bin="$SYSTEMCTL_BIN"
fi

env_files="$("$systemctl_bin" show "$service" -p EnvironmentFiles --value --no-pager 2>/dev/null || true)"
allow_test="no"
[[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]] && allow_test="yes"

mapfile -t paths < <(python3 -c 'import re, sys
allow_test = sys.argv[1] == "yes"
text = sys.stdin.read().strip()
prefixes = ("/etc/xtrapos/", "/opt/xtrapos-staging/", "/usr/share/XtraPOS/")
if allow_test:
    prefixes += ("/tmp/", "/var/tmp/")
paths = re.findall(r"(\S+)\s+\(ignore_errors=(?:yes|no)\)", text)
if not paths:
    paths = [item for item in text.split() if item.startswith("/")]
seen = set()
for path in paths:
    if path.startswith("-"):
        path = path[1:]
    if ".." in path.split("/") or path in seen or not path.startswith(prefixes):
        continue
    seen.add(path)
    print(path)
' "$allow_test" <<<"$env_files")

[[ "${#paths[@]}" -gt 0 ]] || {
  echo "DB_VERIFIER_PATH" >&2
  exit 1
}

name=""
for path in "${paths[@]}"; do
  value="$(python3 -c 'import os, pathlib, sys
path = sys.argv[1]
try:
    fd = os.open(path, os.O_RDONLY)
except OSError:
    sys.exit(2)
os.close(fd)
text = pathlib.Path(path).read_text(errors="replace")
if text.startswith("\ufeff"):
    text = text[1:]
for raw in text.splitlines():
    line = raw.strip()
    if not line or line.startswith("#") or line.startswith(";"):
        continue
    if line.startswith("export "):
        line = line[len("export "):].strip()
    if "=" not in line:
        continue
    key, value = line.split("=", 1)
    if key.strip() != "SqlServer__Database":
        continue
    value = value.strip().strip("\r")
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"\"", "\047"}:
        value = value[1:-1]
    print(value.strip())
    break
' "$path" 2>/dev/null)" || {
    echo "DB_VERIFIER_PERMISSIONS" >&2
    exit 1
  }
  [[ -n "$value" ]] || continue
  if [[ -n "$name" && "$name" != "$value" ]]; then
    echo "DB_VERIFIER_CONFLICT" >&2
    exit 1
  fi
  name="$value"
done

[[ -n "$name" ]] || {
  echo "DB_VERIFIER_NAME" >&2
  exit 1
}
printf 'DB_NAME=%s\n' "$name"
