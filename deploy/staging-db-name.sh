#!/usr/bin/env bash
# Print only SqlServer__Database for xtrapos-staging.service.
# Install as /usr/local/sbin/xtrapos-staging-db-name and allow xtrapos-deploy
# to run that exact path with sudo -n. The installed command takes no arguments,
# calls /usr/bin/systemctl, and prints DB_NAME. It does not consult the caller
# environment for the systemctl binary or the directory allowlist.
set -euo pipefail

approved_environment_files() {
  python3 -c 'import os, re, sys
text = sys.stdin.read().strip()
prefixes = ("/etc/xtrapos/", "/opt/xtrapos-staging/", "/usr/share/XtraPOS/")
if not text or text == "-":
    sys.exit(0)
paths = re.findall(r"(\S+)\s+\(ignore_errors=(?:yes|no)\)", text)
if not paths:
    paths = [item for item in text.split() if item.startswith("/") or item.startswith("-/")]
seen = set()
kept = []
for raw in paths:
    path = raw[1:] if raw.startswith("-") else raw
    parts = path.split("/")
    if not path.startswith("/") or ".." in parts or not path.startswith(prefixes):
        sys.exit(3)
    try:
        resolved = os.path.realpath(path)
    except OSError:
        sys.exit(3)
    if ".." in resolved.split("/") or not resolved.startswith(prefixes):
        sys.exit(3)
    if path in seen:
        continue
    seen.add(path)
    kept.append(path)
if kept:
    print("\n".join(kept))
'
}

read_database_file() {
  python3 -c 'import os, pathlib, sys
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
' "$1"
}

database_name_from_files() {
  local name="" value="" path=""
  [[ "$#" -gt 0 ]] || {
    echo "DB_VERIFIER_PATH" >&2
    return 1
  }
  for path in "$@"; do
    value="$(read_database_file "$path" 2>/dev/null)" || {
      echo "DB_VERIFIER_PERMISSIONS" >&2
      return 1
    }
    [[ -n "$value" ]] || continue
    if [[ -n "$name" && "$name" != "$value" ]]; then
      echo "DB_VERIFIER_CONFLICT" >&2
      return 1
    fi
    name="$value"
  done
  [[ -n "$name" ]] || {
    echo "DB_VERIFIER_NAME" >&2
    return 1
  }
  printf 'DB_NAME=%s\n' "$name"
}

verify_environment_files() {
  local systemctl_bin="${1:-}" env_files="" listed="" status=0
  local -a paths=()
  [[ "$systemctl_bin" == "/usr/bin/systemctl" && -x "$systemctl_bin" ]] || {
    echo "DB_VERIFIER_BIN" >&2
    return 1
  }
  env_files="$("$systemctl_bin" show xtrapos-staging.service -p EnvironmentFiles --value --no-pager 2>/dev/null || true)"
  listed="$(printf '%s\n' "$env_files" | approved_environment_files)" || status=$?
  if [[ "$status" -ne 0 ]]; then
    echo "DB_VERIFIER_UNAPPROVED" >&2
    return 1
  fi
  [[ -n "$listed" ]] || {
    echo "DB_VERIFIER_PATH" >&2
    return 1
  }
  mapfile -t paths <<<"$listed"
  database_name_from_files "${paths[@]}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  [[ "$#" -eq 0 ]] || {
    echo "DB_VERIFIER_ARGS" >&2
    exit 2
  }
  verify_environment_files /usr/bin/systemctl
fi
