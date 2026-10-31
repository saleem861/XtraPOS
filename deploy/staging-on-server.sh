#!/usr/bin/env bash
# Runs on the staging server. The local wrapper feeds it over SSH.
# Actions: probe, prepare, activate, rollback.
# probe is read-only. The other actions touch only /opt/xtrapos-staging and xtrapos-staging.service.
set -euo pipefail

ROOT="/opt/xtrapos-staging"
SERVICE="xtrapos-staging.service"
PORT="5088"
FORBIDDEN_SERVICE="xtrapos-api.service"
FORBIDDEN_PORT="5216"
PUBLIC_NAME="test-pos.sharbelfashion.com"

die() {
  echo "REMOTE_ERROR $*" >&2
  exit 1
}

# Tests may source this file. A production run ignores both variables.
if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
  case "${STAGING_TEST_ROOT:-}" in
    /tmp/*|/var/tmp/*) ;;
    *) die "test root" ;;
  esac
  case "$STAGING_TEST_ROOT" in
    *..*) die "test root" ;;
  esac
  [[ -d "$STAGING_TEST_ROOT" ]] || die "test root"
  ROOT="$(readlink -f "$STAGING_TEST_ROOT")"
  case "$ROOT" in
    /tmp/*|/var/tmp/*) ;;
    *) die "test root" ;;
  esac
else
  action="${1:-}"
  sha="${2:-}"
  version="${3:-}"
  api_enter_before="${4:-}"
  [[ "$sha" =~ ^[0-9a-f]{40}$ ]] || die "sha"
  [[ "$action" =~ ^(probe|prepare|activate|rollback)$ ]] || die "action"
  if [[ "$action" == "prepare" || "$action" == "activate" ]]; then
    [[ "$version" =~ ^[A-Za-z0-9._-]+$ ]] || die "version"
  fi
  case "$ROOT" in
    /opt/xtrapos-staging) ;;
    *) die "root" ;;
  esac
fi

refuse_production_command() {
  if [[ "$*" == *"$FORBIDDEN_SERVICE"* || "$*" == *"$FORBIDDEN_PORT"* ]]; then
    die "production command refused"
  fi
}

database_name() {
  local file="$1" line
  line="$(awk -F= '$1=="SqlServer__Database"{print $2; exit}' "$file" 2>/dev/null || true)"
  if [[ -z "$line" ]] && command -v sudo >/dev/null; then
    line="$(sudo -n awk -F= '$1=="SqlServer__Database"{print $2; exit}' "$file" 2>/dev/null || true)"
  fi
  printf '%s' "$line" | tr -d '\r' | tr -d '"' | tr -d "'"
}

operational_database() {
  case "$1" in
    XtraPOS_Central|XtraPOS_Branch|XtraPOS_Branch_01|XtraPOS_Branch_02|saleemPOSDB|Shop1|Shop2|swstore)
      return 0
      ;;
  esac
  if [[ "$1" == *المقاطعة* ]]; then
    return 0
  fi
  return 1
}

nginx_facts() {
  local dump source status=0
  dump="$(mktemp)"
  chmod 600 "$dump"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${STAGING_NGINX_DUMP:-}" ]]; then
    case "$STAGING_NGINX_DUMP" in
      /tmp/*|/var/tmp/*) ;;
      *) rm -f "$dump"; die "nginx dump" ;;
    esac
    case "$STAGING_NGINX_DUMP" in
      *..*) rm -f "$dump"; die "nginx dump" ;;
    esac
    if [[ ! -f "$STAGING_NGINX_DUMP" ]]; then
      rm -f "$dump"
      die "nginx dump"
    fi
    source="$(readlink -f "$STAGING_NGINX_DUMP")"
    case "$source" in
      /tmp/*|/var/tmp/*) ;;
      *) rm -f "$dump"; die "nginx dump" ;;
    esac
    cp "$source" "$dump"
  elif sudo -n nginx -T >"$dump" 2>/dev/null; then
    :
  elif nginx -T >"$dump" 2>/dev/null; then
    :
  else
    cat /etc/nginx/sites-enabled/* >"$dump" 2>/dev/null || true
  fi
  python3 - "$dump" "$PUBLIC_NAME" "$PORT" "$FORBIDDEN_PORT" <<'PY' || status=$?
import pathlib, re, sys
text = pathlib.Path(sys.argv[1]).read_text(errors="replace")
name, port, forbidden = sys.argv[2:]

def strip_comments(source):
    out = []
    i = 0
    in_single = False
    in_double = False
    while i < len(source):
        ch = source[i]
        if ch == "\\" and i + 1 < len(source) and (in_single or in_double):
            out.append(ch)
            out.append(source[i + 1])
            i += 2
            continue
        if ch == "'" and not in_double:
            in_single = not in_single
        elif ch == '"' and not in_single:
            in_double = not in_double
        elif ch == "#" and not in_single and not in_double:
            while i < len(source) and source[i] != "\n":
                i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)

def server_blocks(source):
    cleaned = strip_comments(source)
    bodies = []
    seen = set()
    occupied = 0
    for match in re.finditer(r"\bserver\b", cleaned):
        if match.start() < occupied:
            continue
        j = match.end()
        while j < len(cleaned) and cleaned[j].isspace():
            j += 1
        if j >= len(cleaned) or cleaned[j] != "{":
            continue
        depth = 0
        k = j
        while k < len(cleaned):
            if cleaned[k] == "{":
                depth += 1
            elif cleaned[k] == "}":
                depth -= 1
                if depth == 0:
                    body = cleaned[j + 1:k]
                    key = "\n".join(line.strip() for line in body.splitlines() if line.strip())
                    if key not in seen:
                        seen.add(key)
                        bodies.append(body)
                    occupied = k + 1
                    break
            k += 1
        else:
            raise SystemExit("NGINX_PARSE")
    return bodies

def server_names(body):
    names = []
    for raw in body.splitlines():
        line = raw.strip().rstrip(";")
        if line.startswith("server_name "):
            names.extend(token.strip("'\"") for token in line.split()[1:])
    return names

def listen_port(value):
    tokens = value.split()
    if not tokens or tokens[0].startswith("unix:"):
        return ""
    address = tokens[0]
    if address.startswith("["):
        return address.split("]:", 1)[1] if "]:" in address else ""
    if ":" in address:
        return address.rsplit(":", 1)[1]
    return address if address.isdigit() else ""

def is_tls(value):
    tokens = value.split()
    return "ssl" in tokens or listen_port(value) == "443"

def listens(body):
    found = []
    for raw in body.splitlines():
        line = raw.strip().rstrip(";")
        if line.startswith("listen "):
            found.append(line[len("listen "):].strip())
    return found

def is_https_redirect(body):
    if re.search(r"(?m)^\s*return\s+(?:301|302|307|308)\s+https://", body):
        return True
    if re.search(r"(?m)^\s*return\s+https://", body):
        return True
    return re.search(r"(?m)^\s*rewrite\s+\S+\s+https://\S+\s+(?:permanent|redirect)\s*;", body) is not None

named = [body for body in server_blocks(text) if name in server_names(body)]
https = []
redirects = 0
for body in named:
    if forbidden in body or "xtrapos-api" in body:
        raise SystemExit("NGINX_PRODUCTION_TARGET")
    if any(is_tls(item) for item in listens(body)):
        https.append(body)
        continue
    if re.search(r"(?m)^\s*proxy_pass\b", body):
        raise SystemExit("NGINX_HTTP_PROXY")
    if not is_https_redirect(body):
        raise SystemExit("NGINX_HTTP_NOT_REDIRECT")
    redirects += 1
if len(https) != 1:
    raise SystemExit(f"NGINX_SERVER_BLOCKS {len(https)}")
block = https[0]
roots = []
proxies = []
for raw in block.splitlines():
    line = raw.strip().rstrip(";")
    if line.startswith("root ") or line.startswith("alias "):
        roots.append(line.split(None, 1)[1])
    if "proxy_pass" in line:
        proxies.append(line.split()[-1])
good = [item for item in proxies if item.endswith(f":{port}") or f":{port}/" in item]
if not good:
    raise SystemExit("NGINX_UPSTREAM_MISMATCH")
print("NGINX_ROOT=" + (roots[0] if roots else ""))
print("NGINX_UPSTREAM=" + good[0])
print("NGINX_HTTP_REDIRECT=" + ("yes" if redirects else "no"))
PY
  rm -f "$dump"
  [[ "$status" -eq 0 ]] || return "$status"
}

unit_value() {
  local value
  value="$(systemctl show "$1" -p "$2" --value --no-pager 2>/dev/null || true)"
  if [[ -z "$value" ]] && command -v sudo >/dev/null; then
    value="$(sudo -n systemctl show "$1" -p "$2" --value --no-pager 2>/dev/null || true)"
  fi
  printf '%s' "$value"
}

listener() {
  local port="$1" line
  line="$(ss -H -ltn "sport = :${port}" 2>/dev/null | awk 'NR==1 { print $4 }' || true)"
  printf '%s' "$line"
}

release_id_from_path() {
  basename "$1"
}

probe() {
  [[ "$(unit_value "$SERVICE" Id)" == "$SERVICE" ]] || die "staging unit missing"
  local fragment work env_files link_target txt_value txt_target staging_listen production_listen db_file db_name key_dir owner
  fragment="$(unit_value "$SERVICE" FragmentPath)"
  work="$(unit_value "$SERVICE" WorkingDirectory)"
  env_files="$(unit_value "$SERVICE" EnvironmentFiles)"
  [[ "$fragment" != *xtrapos-api* ]] || die "staging unit points at production"
  [[ "$work" == "/opt/xtrapos-staging/current/api" ]] || die "staging working directory is not /opt/xtrapos-staging/current/api"
  [[ -L "$ROOT/current" ]] || die "current symlink is missing"
  [[ -f "$ROOT/current.txt" ]] || die "current.txt is missing"
  link_target="$(readlink -f "$ROOT/current")"
  txt_value="$(tr -d '\r\n' <"$ROOT/current.txt")"
  if [[ "$txt_value" == /* ]]; then
    txt_target="$txt_value"
  else
    txt_target="$ROOT/releases/$txt_value"
  fi
  [[ -d "$link_target" && -d "$txt_target" ]] || die "current target is missing"
  [[ "$link_target" == /opt/xtrapos-staging/* && "$txt_target" == /opt/xtrapos-staging/* ]] || die "current target is outside the staging root"
  [[ "$(release_id_from_path "$link_target")" == "$(release_id_from_path "$txt_target")" ]] || die "current symlink and current.txt disagree"
  staging_listen="$(listener "$PORT")"
  [[ "$staging_listen" == "127.0.0.1:${PORT}" || "$staging_listen" == "[::1]:${PORT}" ]] || die "staging port is not loopback ${PORT}"
  production_listen="$(listener "$FORBIDDEN_PORT")"
  [[ "$production_listen" != *":${PORT}" ]] || die "production listener collided with staging"
  nginx_facts
  db_name=""
  db_file=""
  if [[ -n "$env_files" && "$env_files" != "-" ]]; then
    db_file="${env_files%% *}"
    db_name="$(database_name "$db_file")"
  fi
  if [[ -z "$db_name" && -f /etc/xtrapos/staging.env ]]; then
    db_file="/etc/xtrapos/staging.env"
    db_name="$(database_name "$db_file")"
  fi
  [[ -n "$db_name" ]] || die "staging database name is unreadable"
  if operational_database "$db_name"; then
    die "refusing operational database"
  fi
  if [[ -d /usr/share/XtraPOS/data-protection-keys ]]; then
    key_dir="/usr/share/XtraPOS/data-protection-keys"
  elif [[ -d "$ROOT/shared/data-protection-keys" ]]; then
    key_dir="$ROOT/shared/data-protection-keys"
  else
    key_dir="missing"
  fi
  owner="$(stat -c '%U:%G %a' "$ROOT" 2>/dev/null || echo unknown)"
  printf 'PROBE_OK=yes\n'
  printf 'POINTER=both\n'
  printf 'CURRENT_ID=%s\n' "$(release_id_from_path "$link_target")"
  printf 'CURRENT_TARGET=%s\n' "$link_target"
  printf 'WORKING_DIRECTORY=%s\n' "$work"
  printf 'STAGING_LISTEN=%s\n' "$staging_listen"
  printf 'PRODUCTION_PORT=%s\n' "$FORBIDDEN_PORT"
  printf 'ENV_FILE=%s\n' "$db_file"
  printf 'DB_NAME=%s\n' "$db_name"
  printf 'KEY_DIR=%s\n' "$key_dir"
  printf 'ROOT_OWNER=%s\n' "$owner"
  printf 'API_ENTER=%s\n' "$(unit_value "$FORBIDDEN_SERVICE" ActiveEnterTimestamp)"
}

prepare() {
  local dest="$ROOT/incoming/$version"
  [[ "$dest" == /opt/xtrapos-staging/incoming/* ]] || die "incoming path"
  mkdir -p "$dest"
  printf 'PREPARE_OK=%s\n' "$dest"
}

preserve_app_data() {
  local new_api="$1" old_api="$2" shared="$ROOT/shared/app-data" source=""
  [[ "$new_api" == "$ROOT"/* && "$new_api" != "$old_api" ]] || die "refusing to retarget the live App_Data"
  [[ -z "$old_api" || "$old_api" == "$ROOT"/* ]] || die "previous release is outside the staging root"
  mkdir -p "$ROOT/shared"
  if [[ ! -e "$shared" && ! -L "$shared" ]]; then
    if [[ -d "$old_api/App_Data" && ! -L "$old_api/App_Data" ]]; then
      cp -a "$old_api/App_Data" "$shared"
    elif [[ -L "$old_api/App_Data" ]]; then
      source="$(readlink -f "$old_api/App_Data" || true)"
      case "$source" in
        "$ROOT"/*)
          if [[ -d "$source" && ! -L "$source" ]]; then
            mkdir -p "$shared"
            cp -a "$source/." "$shared/"
          else
            mkdir -p "$shared"
          fi
          ;;
        *)
          mkdir -p "$shared"
          ;;
      esac
    else
      mkdir -p "$shared"
    fi
  fi
  if [[ -e "$new_api/App_Data" || -L "$new_api/App_Data" ]]; then
    rm -rf "$new_api/App_Data"
  fi
  ln -s "$shared" "$new_api/App_Data"
}

backup_pointer() {
  local dir="$ROOT/backups/$(date -u +%Y%m%dT%H%M%SZ)-${sha:0:12}"
  mkdir -p "$dir"
  if [[ -L "$ROOT/current" ]]; then
    printf 'symlink\n' >"$dir/pointer-type"
    readlink -f "$ROOT/current" >"$dir/current-target"
  fi
  if [[ -f "$ROOT/current.txt" ]]; then
    cp -a "$ROOT/current.txt" "$dir/current.txt"
  fi
  if [[ -f "$ROOT/previous.txt" ]]; then
    cp -a "$ROOT/previous.txt" "$dir/previous.txt"
  fi
  printf '%s\n' "$dir"
}

restart_staging_only() {
  local before after body
  before="$(unit_value "$FORBIDDEN_SERVICE" ActiveEnterTimestamp)"
  if ! systemctl restart "$SERVICE" 2>/dev/null; then
    sudo -n systemctl restart "$SERVICE" || die "cannot restart ${SERVICE}; sudo is limited to that unit"
  fi
  after="$(unit_value "$FORBIDDEN_SERVICE" ActiveEnterTimestamp)"
  [[ "$before" == "$after" ]] || die "production service timestamp changed"
  systemctl is-active --quiet "$SERVICE" 2>/dev/null || sudo -n systemctl is-active --quiet "$SERVICE" || return 1
  body="$(curl -fsS --max-time 20 "http://127.0.0.1:${PORT}/health" || true)"
  grep -q Healthy <<<"$body" || return 1
}

activate_pointers() {
  local release="$1" version="$2" link_tmp text_tmp
  link_tmp="$(mktemp -u "$ROOT/.current.XXXXXX")"
  ln -s "$release" "$link_tmp"
  mv -Tf "$link_tmp" "$ROOT/current"
  text_tmp="$(mktemp "$ROOT/.current.txt.XXXXXX")"
  printf '%s\n' "$version" >"$text_tmp"
  mv -f "$text_tmp" "$ROOT/current.txt"
}

activate() {
  local incoming="$ROOT/incoming/$version" release="$ROOT/releases/$version" old_target backup manifest_sha old_api
  [[ -f "$incoming/release.json" ]] || die "uploaded release is missing"
  manifest_sha="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sha"])' "$incoming/release.json")"
  [[ "$manifest_sha" == "$sha" ]] || die "uploaded sha mismatch"
  probe >/tmp/xtrapos-probe.$$
  old_target="$(awk -F= '/^CURRENT_TARGET=/{print $2}' /tmp/xtrapos-probe.$$)"
  rm -f /tmp/xtrapos-probe.$$
  [[ "$release" == /opt/xtrapos-staging/releases/* ]] || die "release path"
  mkdir -p "$ROOT/releases"
  rm -rf "$release"
  mv "$incoming" "$release"
  if [[ -d "$old_target/api" ]]; then
    old_api="$old_target/api"
  elif [[ -d "$old_target" ]]; then
    old_api="$old_target"
  else
    old_api=""
  fi
  if [[ -n "$old_api" ]]; then
    preserve_app_data "$release/api" "$old_api"
  fi
  backup="$(backup_pointer)"
  cp -a "$ROOT/current.txt" "$ROOT/previous.txt"
  activate_pointers "$release" "$version"
  if ! restart_staging_only; then
    activate_pointers "$old_target" "$(basename "$old_target")"
    restart_staging_only || die "previous staging release did not become healthy"
    die "activate failed and the previous pointer was restored"
  fi
  printf 'ACTIVATE_OK=%s\n' "$release"
}

restore_latest_backup() {
  local latest type target
  latest="$(find "$ROOT/backups" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
  [[ -n "$latest" ]] || die "no staging backup"
  if [[ -f "$latest/pointer-type" && "$(cat "$latest/pointer-type")" == "symlink" ]]; then
    target="$(cat "$latest/current-target")"
    [[ "$target" == /opt/xtrapos-staging/* ]] || die "backup target is outside the staging root"
    ln -sfn "$target" "$ROOT/current"
    printf '%s\n' "$(basename "$target")" >"$ROOT/current.txt"
  elif [[ -f "$latest/current.txt" ]]; then
    cp -a "$latest/current.txt" "$ROOT/current.txt"
    if [[ -d "$ROOT/releases/$(tr -d '\r\n' <"$ROOT/current.txt")" ]]; then
      ln -sfn "$ROOT/releases/$(tr -d '\r\n' <"$ROOT/current.txt")" "$ROOT/current"
    fi
  elif [[ -f "$ROOT/previous.txt" ]]; then
    cp -a "$ROOT/previous.txt" "$ROOT/current.txt"
  else
    die "backup has no pointer"
  fi
}

rollback() {
  probe >/dev/null
  restore_latest_backup
  restart_staging_only
  printf 'ROLLBACK_OK=yes\n'
}

if [[ "${STAGING_SOURCE_ONLY:-}" != "yes" ]]; then
  case "$action" in
    probe) probe ;;
    prepare) prepare ;;
    activate) activate ;;
    rollback) rollback ;;
  esac
fi
