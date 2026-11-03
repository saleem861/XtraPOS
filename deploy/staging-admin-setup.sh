#!/usr/bin/env bash
# One-time staging setup for a Linux administrator.
# Run on 69.197.178.239 as root. This does not grant unrestricted sudo,
# does not restart production, and does not open SQL.
set -euo pipefail

ROOT="/opt/xtrapos-staging"
SERVICE="xtrapos-staging.service"
FORBIDDEN_SERVICE="xtrapos-api.service"
DEPLOY_USER="xtrapos-deploy"
HELPER_SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/staging-activate-helper.sh"
HELPER_TARGET="/usr/local/sbin/xtrapos-staging-activate"
ENV_FILE="/etc/xtrapos/staging.env"
NGINX_LINK="/etc/nginx/sites-enabled/default"
NGINX_CANONICAL="/etc/nginx/sites-available/default"
SUDOERS_FILE="/etc/sudoers.d/xtrapos-staging"

die() {
  echo "SETUP_ERROR $*" >&2
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
fi

nginx_test() {
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" && -n "${STAGING_NGINX_BIN:-}" ]]; then
    "$STAGING_NGINX_BIN" -t
    return
  fi
  nginx -t
}

nginx_reload() {
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
    if [[ -n "${STAGING_NGINX_RELOAD_BIN:-}" ]]; then
      "$STAGING_NGINX_RELOAD_BIN"
    fi
    return
  fi
  systemctl reload nginx
}

production_stamp() {
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
    printf '%s' "${STAGING_PRODUCTION_STAMP:-unchanged}"
    return 0
  fi
  systemctl show "$FORBIDDEN_SERVICE" -p ActiveEnterTimestamp --value
}

nginx_paths() {
  local link="$NGINX_LINK" expected="$NGINX_CANONICAL"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
    link="${STAGING_NGINX_LINK:-$link}"
    expected="${STAGING_NGINX_CANONICAL:-$expected}"
    case "$link" in
      /tmp/*|/var/tmp/*) ;;
      *) die "nginx test path" ;;
    esac
    case "$expected" in
      /tmp/*|/var/tmp/*) ;;
      *) die "nginx test path" ;;
    esac
    case "${link}${expected}" in
      *..*) die "nginx test path" ;;
    esac
  fi
  is_symlink "$link" || die "nginx sites-enabled entry must stay a symlink"
  local literal resolved
  literal="$(readlink "$link")"
  [[ -n "$literal" ]] || die "nginx symlink target"
  resolved="$(readlink -f "$link")"
  [[ "$resolved" == "$expected" ]] || die "nginx symlink target is not the verified file"
  is_symlink "$resolved" && die "nginx canonical file"
  [[ -f "$resolved" ]] || die "nginx canonical file"
  [[ "$(readlink -f "$resolved")" == "$resolved" ]] || die "nginx canonical file"
  printf '%s\n%s\n%s\n' "$link" "$literal" "$resolved"
}

write_nginx_file() {
  local source="$1" dest="$2" mode="$3" owner="$4" link="$5" literal="$6"
  cat "$source" >"$dest"
  chmod "$mode" "$dest"
  chown "$owner" "$dest"
  is_symlink "$link" || die "nginx symlink changed"
  [[ "$(readlink "$link")" == "$literal" ]] || die "nginx symlink changed"
  [[ "$(readlink -f "$link")" == "$dest" ]] || die "nginx symlink changed"
}

apply_nginx_release_json() {
  local info link literal canonical mode owner backup updated before after code
  info="$(nginx_paths)"
  link="${info%%$'\n'*}"
  info="${info#*$'\n'}"
  literal="${info%%$'\n'*}"
  canonical="${info#*$'\n'}"
  mode="$(stat -c '%a' "$canonical")"
  owner="$(stat -c '%u:%g' "$canonical")"
  backup="$(mktemp)"
  cat "$canonical" >"$backup"
  updated="$(mktemp)"
  set +e
  awk '
    /^[[:space:]]*server[[:space:]]*\{/ { saw_name = 0; saw_root = 0; in_target = 0 }
    /server_name test-pos\.sharbelfashion\.com;/ { saw_name = 1 }
    /root \/opt\/xtrapos-staging\/current\/web;/ { saw_root = 1 }
    {
      if (saw_name && saw_root) in_target = 1
      if (in_target && index($0, "location = /release.json") > 0) already = 1
      if (in_target && !already && $0 == "    location / {") {
        print "    location = /release.json {"
        print "        default_type application/json;"
        print "        add_header Cache-Control \"no-store\";"
        print "        try_files /release.json =404;"
        print "    }"
        print ""
        changed = 1
      }
      print
    }
    END {
      if (!changed && !already) exit 2
      if (changed) exit 10
    }
  ' "$canonical" >"$updated"
  code=$?
  set -e
  if [[ "$code" -eq 2 ]]; then
    rm -f "$backup" "$updated"
    die "staging HTTPS server block was not found"
  fi
  if [[ "$code" -ne 0 && "$code" -ne 10 ]]; then
    rm -f "$backup" "$updated"
    die "nginx configuration was not changed"
  fi
  if [[ "$code" -eq 0 ]]; then
    rm -f "$backup" "$updated"
    is_symlink "$link" || die "nginx symlink changed"
    [[ "$(readlink "$link")" == "$literal" ]] || die "nginx symlink changed"
    echo "NGINX_RELEASE_JSON=already-present"
    return 0
  fi
  write_nginx_file "$updated" "$canonical" "$mode" "$owner" "$link" "$literal"
  rm -f "$updated"
  if ! nginx_test; then
    write_nginx_file "$backup" "$canonical" "$mode" "$owner" "$link" "$literal"
    rm -f "$backup"
    nginx_test || die "restored nginx configuration is invalid"
    die "nginx test failed and the previous configuration was restored"
  fi
  before="$(production_stamp)"
  if ! nginx_reload; then
    write_nginx_file "$backup" "$canonical" "$mode" "$owner" "$link" "$literal"
    rm -f "$backup"
    nginx_test || die "restored nginx configuration is invalid"
    nginx_reload || die "could not reload the restored nginx configuration"
    die "nginx reload failed and the previous configuration was restored"
  fi
  after="$(production_stamp)"
  if [[ "$before" != "$after" ]]; then
    write_nginx_file "$backup" "$canonical" "$mode" "$owner" "$link" "$literal"
    rm -f "$backup"
    nginx_test || die "restored nginx configuration is invalid"
    nginx_reload || true
    die "production service timestamp changed during nginx reload"
  fi
  rm -f "$backup"
  echo "NGINX_RELEASE_JSON=inserted"
}

install_sudoers() {
  local tmp
  tmp="$(mktemp)"
  cat >"$tmp" <<EOF
$DEPLOY_USER ALL=(root) NOPASSWD: /usr/local/sbin/xtrapos-staging-db-name, $HELPER_TARGET
EOF
  chmod 440 "$tmp"
  if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
    cat "$tmp"
    rm -f "$tmp"
    return 0
  fi
  if ! visudo -cf "$tmp" >/dev/null; then
    rm -f "$tmp"
    die "sudoers validation failed"
  fi
  install -o root -g root -m 440 "$tmp" "$SUDOERS_FILE"
  rm -f "$tmp"
}

ensure_real_dir() {
  local path="$1" mode="$2" owner="$3" group="$4"
  [[ "$path" == "$ROOT" || "$path" == "$ROOT/"* ]] || die "directory escapes staging"
  [[ "$path" != *..* ]] || die "directory escapes staging"
  if is_symlink "$ROOT"; then
    die "refusing a symlink at $ROOT"
  fi
  [[ -d "$ROOT" ]] || die "staging root is missing"
  local rest="" cursor="$ROOT" part resolved
  if [[ "$path" != "$ROOT" ]]; then
    rest="${path#"$ROOT"/}"
  fi
  if [[ -n "$rest" ]]; then
    local IFS=/
    for part in $rest; do
      [[ -n "$part" && "$part" != "." && "$part" != ".." ]] || die "directory escapes staging"
      cursor="$cursor/$part"
      if is_symlink "$cursor"; then
        die "refusing a symlink at $cursor"
      fi
      if [[ ! -d "$cursor" ]]; then
        mkdir "$cursor"
      fi
      resolved="$(readlink -f "$cursor")"
      [[ "$resolved" == "$cursor" ]] || die "directory escapes staging"
      [[ "$resolved" == "$ROOT" || "$resolved" == "$ROOT"/* ]] || die "directory escapes staging"
    done
  fi
  chmod "$mode" "$path"
  if [[ "${STAGING_SOURCE_ONLY:-}" != "yes" ]]; then
    chown "$owner:$group" "$path"
  fi
}

if [[ "${STAGING_SOURCE_ONLY:-}" == "yes" ]]; then
  return 0 2>/dev/null || exit 0
fi

[[ "$(id -u)" -eq 0 ]] || die "run as root"
[[ -d "$ROOT" ]] && ! is_symlink "$ROOT" || die "staging root is missing"
[[ "$(readlink -f "$ROOT")" == "$ROOT" ]] || die "staging root must not be a symlink"
id "$DEPLOY_USER" >/dev/null
[[ -f "$HELPER_SOURCE" ]] && ! is_symlink "$HELPER_SOURCE" || die "staging-activate-helper.sh must sit beside this script"
service_user="$(systemctl show "$SERVICE" -p User --value)"
[[ -n "$service_user" && "$service_user" != "root" ]] || die "staging service user is missing"

install -o root -g root -m 755 "$HELPER_SOURCE" "$HELPER_TARGET"
install_sudoers

ensure_real_dir "$ROOT" 2775 "$service_user" "$DEPLOY_USER"
for directory in releases incoming backups shared; do
  ensure_real_dir "$ROOT/$directory" 2775 "$service_user" "$DEPLOY_USER"
done
ensure_real_dir "$ROOT/shared/app-data" 750 "$service_user" "$service_user"
ensure_real_dir "$ROOT/shared/data-protection-keys" 700 "$service_user" "$service_user"

if is_symlink "$ENV_FILE"; then
  die "staging environment file must not be a symlink"
fi
if [[ ! -f "$ENV_FILE" ]]; then
  install -o root -g root -m 600 /dev/null "$ENV_FILE"
fi
chown root:root "$ENV_FILE"
chmod 600 "$ENV_FILE"
append_env() {
  local key="$1" value="$2"
  if grep -q -E "^${key}=" "$ENV_FILE"; then
    return 0
  fi
  printf '%s=%s\n' "$key" "$value" >>"$ENV_FILE"
}
append_env ASPNETCORE_ENVIRONMENT Staging
append_env Passkey__RpId test-pos.sharbelfashion.com
append_env Passkey__PublicBaseUrl https://test-pos.sharbelfashion.com
append_env Passkey__Origins__0 https://test-pos.sharbelfashion.com
append_env DataProtection__KeyPath /opt/xtrapos-staging/shared/data-protection-keys
append_env DataProtection__ApplicationName XtraPOS.Staging

apply_nginx_release_json

echo "SETUP_OK"
echo "Deploy account can write $ROOT and run $HELPER_TARGET."
echo "Data protection keys: $ROOT/shared/data-protection-keys"
echo "Production service was not restarted."
