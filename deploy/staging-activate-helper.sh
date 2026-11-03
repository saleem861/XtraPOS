#!/usr/bin/env bash
# Root-owned staging activation helper.
# Install as /usr/local/sbin/xtrapos-staging-activate.
# It accepts only write-central and activate, and restarts only xtrapos-staging.service.
set -euo pipefail

ROOT="/opt/xtrapos-staging"
SERVICE="xtrapos-staging.service"
FORBIDDEN_SERVICE="xtrapos-api.service"
BRANCH_DB="XtraPOS_Branch_Test_Staging"
CENTRAL_DB="XtraPOS_Central_Test_Staging"
ENV_FILE="/etc/xtrapos/staging.env"
KEYS="$ROOT/shared/data-protection-keys"
SHARED_DATA="$ROOT/shared/app-data"

die() {
  echo "STAGING_ERROR $*" >&2
  exit 1
}

is_symlink() {
  [[ -L "$1" ]]
}

if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
  case "${STAGING_TEST_ROOT:-}" in
    /tmp/*|/var/tmp/*) ;;
    *) die "test root" ;;
  esac
  case "$STAGING_TEST_ROOT" in
    *..*) die "test root" ;;
  esac
  ROOT="$(readlink -f "$STAGING_TEST_ROOT")"
  case "$ROOT" in
    /tmp/*|/var/tmp/*) ;;
    *) die "test root" ;;
  esac
  KEYS="$ROOT/shared/data-protection-keys"
  SHARED_DATA="$ROOT/shared/app-data"
fi

validate_version() {
  local version="$1"
  [[ "$version" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] || die "version"
  [[ "$version" != *..* ]] || die "version"
}

assert_canonical_dir() {
  local path="$1"
  [[ -d "$path" ]] && ! is_symlink "$path" || die "directory"
  local resolved
  resolved="$(readlink -f "$path")"
  [[ "$resolved" == "$path" ]] || die "directory is not canonical"
  [[ "$resolved" == "$ROOT" || "$resolved" == "$ROOT"/* ]] || die "directory escapes staging"
}

validated_release() {
  local version="$1"
  validate_version "$version"
  assert_canonical_dir "$ROOT"
  assert_canonical_dir "$ROOT/releases"
  local release="$ROOT/releases/$version"
  assert_canonical_dir "$release"
  local api="$release/api" web="$release/web"
  assert_canonical_dir "$api"
  assert_canonical_dir "$web"
  [[ -f "$api/XtraPOS.Api.dll" ]] && ! is_symlink "$api/XtraPOS.Api.dll" || die "release dll"
  [[ -f "$web/index.html" ]] && ! is_symlink "$web/index.html" || die "release web"
  [[ -f "$release/release.json" ]] && ! is_symlink "$release/release.json" || die "release manifest"
  printf '%s\n' "$release"
}

load_staging_env() {
  [[ -f "$ENV_FILE" ]] && ! is_symlink "$ENV_FILE" || die "staging environment file"
  local resolved line key value
  resolved="$(readlink -f "$ENV_FILE")"
  [[ "$resolved" == "$ENV_FILE" ]] || die "staging environment file"
  declare -A seen=()
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ "$line" =~ ^[[:space:]]*(#|$) ]] && continue
    [[ "$line" =~ ^[[:space:]]*\; ]] && continue
    if [[ "$line" =~ ^[[:space:]]*export[[:space:]]+(.+)$ ]]; then
      line="${BASH_REMATCH[1]}"
    fi
    [[ "$line" == *=* ]] || die "staging environment file"
    key="${line%%=*}"
    value="${line#*=}"
    key="${key#"${key%%[![:space:]]*}"}"
    key="${key%"${key##*[![:space:]]}"}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || die "staging environment file"
    case "$key" in
      SqlServer__*|Passkey__*|DataProtection__*|ASPNETCORE_*) ;;
      *) continue ;;
    esac
    [[ -z "${seen[$key]:-}" ]] || die "staging environment file assigns ${key} more than once"
    seen["$key"]=1
    if [[ ${#value} -ge 2 ]]; then
      local first="${value:0:1}" last="${value: -1}"
      if [[ "$first" == "$last" && ( "$first" == '"' || "$first" == "'" ) ]]; then
        value="${value:1:${#value}-2}"
      fi
    fi
    printf -v "$key" '%s' "$value"
    export "$key"
  done <"$ENV_FILE"
}

prepare_private_dir() {
  local path="$1" mode="$2"
  [[ "$path" == "$ROOT/"* && "$path" != *..* ]] || die "directory escapes staging"
  assert_canonical_dir "$ROOT"
  local rest="${path#"$ROOT"/}" cursor="$ROOT" part
  local IFS=/
  for part in $rest; do
    [[ -n "$part" && "$part" != "." && "$part" != ".." ]] || die "directory"
    cursor="$cursor/$part"
    if is_symlink "$cursor"; then
      die "refusing a symlink"
    fi
    if [[ ! -d "$cursor" ]]; then
      mkdir "$cursor"
    fi
    assert_canonical_dir "$cursor"
  done
  chmod "$mode" "$path"
  if [[ "${STAGING_SOURCE_ONLY:-}" != "yes" ]]; then
    chown "$service_user:$service_user" "$path"
  fi
}

link_app_data() {
  local api="$1"
  assert_canonical_dir "$api"
  [[ "$api" == "$ROOT/releases/"*/api ]] || die "api path"
  prepare_private_dir "$SHARED_DATA" 750
  local app_data="$api/App_Data"
  if is_symlink "$app_data"; then
    local target
    target="$(readlink -f "$app_data")"
    [[ "$target" == "$SHARED_DATA" ]] || die "App_Data points outside shared staging data"
    return 0
  fi
  if [[ -e "$api/central-connection.json" ]] && ! is_symlink "$api/central-connection.json"; then
    die "release contains a central connection file"
  fi
  if [[ -d "$app_data" ]]; then
    assert_canonical_dir "$app_data"
    if [[ -f "$app_data/central-connection.json" ]] && ! is_symlink "$app_data/central-connection.json"; then
      die "release contains a central connection file"
    fi
    mv "$app_data" "$api/App_Data.packaged.$$" || die "App_Data"
  elif [[ -e "$app_data" ]]; then
    die "App_Data"
  fi
  ln -s "$SHARED_DATA" "$app_data"
}

central_name() {
  local path="$1" resolved
  [[ -f "$path" ]] && ! is_symlink "$path" || die "central connection"
  resolved="$(readlink -f "$path")"
  [[ "$resolved" == "$SHARED_DATA/central-connection.json" ]] || die "central connection"
  python3 -c 'import json, pathlib, sys
document = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
name = document.get("databaseName") if isinstance(document, dict) else None
print(name.strip() if isinstance(name, str) else "missing")
' "$resolved"
}

production_stamp() {
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
    printf '%s' "${STAGING_PRODUCTION_STAMP:-unchanged}"
    return 0
  fi
  systemctl show "$FORBIDDEN_SERVICE" -p ActiveEnterTimestamp --value
}

reject_production_change() {
  local before="$1" afte
  after="$(production_stamp)"
  [[ "$before" == "$after" ]] || die "production service timestamp changed"
}

restart_staging_only() {
  local before
  before="$(production_stamp)"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${STAGING_RESTART_BIN:-}" ]]; then
    "$STAGING_RESTART_BIN" || return 1
  else
    systemctl restart "$SERVICE" || return 1
    systemctl is-active --quiet "$SERVICE" || return 1
  fi
  reject_production_change "$before"
}

health_probe() {
  local body code curl_bin status_re
  curl_bin="curl"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${STAGING_CURL_BIN:-}" ]]; then
    curl_bin="$STAGING_CURL_BIN"
  fi
  body="$("$curl_bin" -sS --max-time 20 -H "Host: test-pos.sharbelfashion.com" --write-out $'\n%{http_code}' "http://127.0.0.1:5088/health" 2>/dev/null || true)"
  code="${body##*$'\n'}"
  body="${body%$'\n'$code}"
  [[ "$code" == "200" ]] || return 1
  status_re='"status"[[:space:]]*:[[:space:]]*"Healthy"'
  [[ "$body" =~ $status_re ]]
}

health_ok() {
  local interval=2 deadline=90 start now elapsed
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
    if [[ -n "${STAGING_HEALTH_INTERVAL:-}" ]]; then
      interval="$STAGING_HEALTH_INTERVAL"
    fi
    if [[ -n "${STAGING_HEALTH_DEADLINE:-}" ]]; then
      deadline="$STAGING_HEALTH_DEADLINE"
    fi
  fi
  [[ "$interval" =~ ^[0-9]+$ && "$deadline" =~ ^[1-9][0-9]*$ ]] || die "health timing"
  start="$(date +%s)"
  while true; do
    now="$(date +%s)"
    elapsed=$((now - start))
    if (( elapsed >= deadline )); then
      break
    fi
    if health_probe; then
      return 0
    fi
    now="$(date +%s)"
    elapsed=$((now - start))
    if (( elapsed + interval > deadline )); then
      break
    fi
    sleep "$interval"
  done
  echo "STAGING_ERROR staging health check did not return HTTP 200 and status Healthy within ${deadline} seconds" >&2
  return 1
}

wait_until_healthy_or_restore() {
  local backup="$1" before="$2"
  if ! health_ok; then
    restore_and_restart "$backup" "$before"
    die "staging health check failed and the previous release was restored"
  fi
}

backup_pointer() {
  local version="$1" backup target
  assert_canonical_dir "$ROOT"
  if is_symlink "$ROOT/backups"; then
    die "backups symlink"
  fi
  if [[ ! -d "$ROOT/backups" ]]; then
    mkdir "$ROOT/backups"
  fi
  assert_canonical_dir "$ROOT/backups"
  backup="$ROOT/backups/$(date -u +%Y%m%dT%H%M%SZ)-${version:0:12}"
  if is_symlink "$backup"; then
    die "backup"
  fi
  mkdir "$backup"
  assert_canonical_dir "$backup"
  if is_symlink "$ROOT/current"; then
    target="$(readlink -f "$ROOT/current")"
    [[ -d "$target" && "$target" == "$ROOT/releases/"* ]] || die "current release escapes staging"
    printf '%s\n' "$target" >"$backup/current-target"
  fi
  if is_symlink "$ROOT/current.txt"; then
    die "current.txt must not be a symlink"
  fi
  if [[ -f "$ROOT/current.txt" ]]; then
    cp -a "$ROOT/current.txt" "$backup/current.txt"
    cp -a "$ROOT/current.txt" "$ROOT/previous.txt"
  fi
  printf '%s\n' "$backup"
}

switch_pointer() {
  local release="$1" version="$2" link_tmp text_tmp
  assert_canonical_dir "$release"
  link_tmp="$(mktemp -u "$ROOT/.current.XXXXXX")"
  ln -s "$release" "$link_tmp"
  mv -Tf "$link_tmp" "$ROOT/current"
  if is_symlink "$ROOT/current.txt"; then
    die "current.txt must not be a symlink"
  fi
  text_tmp="$(mktemp "$ROOT/.current.txt.XXXXXX")"
  printf '%s\n' "$version" >"$text_tmp"
  mv -f "$text_tmp" "$ROOT/current.txt"
}

restore_pointer() {
  local backup="$1" target resolved link_tmp text_tmp
  [[ -d "$backup" ]] && ! is_symlink "$backup" || die "backup"
  [[ "$(readlink -f "$backup")" == "$ROOT/backups/"* ]] || die "backup escapes staging"
  [[ -f "$backup/current-target" ]] && ! is_symlink "$backup/current-target" || die "backup target"
  target="$(tr -d '\r\n' <"$backup/current-target")"
  [[ "$target" == "$ROOT/releases/"* && "$target" != *..* ]] || die "backup target"
  [[ -d "$target" ]] && ! is_symlink "$target" || die "backup target"
  resolved="$(readlink -f "$target")"
  [[ "$resolved" == "$target" && "$resolved" == "$ROOT/releases/"* ]] || die "backup target escapes staging"
  link_tmp="$(mktemp -u "$ROOT/.current.XXXXXX")"
  ln -s "$resolved" "$link_tmp"
  mv -Tf "$link_tmp" "$ROOT/current"
  if [[ -f "$backup/current.txt" ]] && ! is_symlink "$backup/current.txt"; then
    if is_symlink "$ROOT/current.txt"; then
      die "current.txt must not be a symlink"
    fi
    text_tmp="$(mktemp "$ROOT/.current.txt.XXXXXX")"
    cp -a "$backup/current.txt" "$text_tmp"
    mv -f "$text_tmp" "$ROOT/current.txt"
  fi
}

restore_and_restart() {
  local backup="$1" before="$2"
  restore_pointer "$backup"
  restart_staging_only || die "rollback restart failed"
  reject_production_change "$before"
}

run_api() {
  local api="$1"
  shift
  local dll="$api/XtraPOS.Api.dll" before home
  assert_canonical_dir "$api"
  [[ -f "$dll" ]] && ! is_symlink "$dll" || die "release dll"
  load_staging_env
  export ASPNETCORE_ENVIRONMENT=Staging
  export ASPNETCORE_CONTENTROOT="$api"
  export DataProtection__KeyPath="$KEYS"
  export DataProtection__ApplicationName=XtraPOS.Staging
  unset LD_PRELOAD LD_LIBRARY_PATH BASH_ENV ENV
  before="$(production_stamp)"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${STAGING_DOTNET_BIN:-}" ]]; then
    "$STAGING_DOTNET_BIN" "$dll" "$@" || return 1
  else
    home="$(getent passwd "$service_user" | awk -F: 'NR==1 { print $6 }')"
    [[ -n "$home" && "$home" == /* ]] || die "service home"
    (
      unset LD_PRELOAD LD_LIBRARY_PATH BASH_ENV ENV
      export HOME="$home"
      export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
      export USER="$service_user" LOGNAME="$service_user"
      exec runuser -u "$service_user" --preserve-environment -- dotnet "$dll" "$@"
    )
  fi
  reject_production_change "$before"
}

write_central() {
  local version="$2" server="$3" port="$4" username="$5" release api name
  validate_version "$version"
  [[ "$port" =~ ^[0-9]+$ && "$port" -ge 1 && "$port" -le 65535 ]] || die "port"
  [[ "$server" =~ ^[A-Za-z0-9._-]+$ ]] || die "server"
  [[ "$username" =~ ^[A-Za-z0-9._-]+$ ]] || die "username"
  release="$(validated_release "$version")"
  api="$release/api"
  prepare_private_dir "$KEYS" 700
  link_app_data "$api"
  run_api "$api" \
    --write-staging-central-connection \
    --confirm-database "$CENTRAL_DB" \
    --server "$server" \
    --port "$port" \
    --username "$username"
  name="$(central_name "$SHARED_DATA/central-connection.json")"
  [[ "$name" == "$CENTRAL_DB" ]] || die "saved central database was not $CENTRAL_DB"
  echo "CENTRAL_SAVED=$CENTRAL_DB"
}

activate() {
  local version="$2" release api name branch before backup
  release="$(validated_release "$version")"
  api="$release/api"
  prepare_private_dir "$KEYS" 700
  link_app_data "$api"
  name="$(central_name "$SHARED_DATA/central-connection.json")"
  [[ "$name" == "$CENTRAL_DB" ]] || die "refusing activation while central database is $name"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${STAGING_BRANCH_DB:-}" ]]; then
    branch="$STAGING_BRANCH_DB"
  else
    branch="$(/usr/local/sbin/xtrapos-staging-db-name | sed -n 's/^DB_NAME=//p')"
  fi
  [[ "$branch" == "$BRANCH_DB" ]] || die "refusing activation while branch database is ${branch:-unknown}"
  before="$(production_stamp)"
  run_api "$api" --apply-branch-migrations --confirm-database "$BRANCH_DB"
  run_api "$api" --apply-central-migrations --confirm-database "$CENTRAL_DB"
  backup="$(backup_pointer "$version")"
  switch_pointer "$release" "$version"
  if ! restart_staging_only; then
    restore_and_restart "$backup" "$before"
    die "staging restart failed and the previous release was restored"
  fi
  wait_until_healthy_or_restore "$backup" "$before"
  echo "ACTIVATE_OK=$version"
}

if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
  return 0 2>/dev/null || exit 0
fi

[[ "$(id -u)" -eq 0 ]] || die "run as root"
action="${1:-}"
service_user="$(systemctl show "$SERVICE" -p User --value)"
[[ -n "$service_user" && "$service_user" != "root" ]] || die "staging service user is missing"

case "$action" in
  write-central) write_central "$@" ;;
  activate) activate "$@" ;;
  *) die "action must be write-central or activate" ;;
esac
