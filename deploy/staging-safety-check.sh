#!/usr/bin/env bash
# Non-deployment checks for the staging scripts. This does not open a network connection.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "::error::$1" >&2
  exit 1
}

for file in deploy/staging-ssh.sh deploy/staging-on-server.sh deploy/staging-remote-activate.sh deploy/staging-rollback.sh deploy/staging-preflight.sh deploy/scan-release.sh deploy/staging-db-name.sh; do
  bash -n "$file"
done

scope=(
  deploy/staging-ssh.sh
  deploy/staging-on-server.sh
  deploy/staging-remote-activate.sh
  deploy/staging-rollback.sh
  .github/workflows/deploy-staging.yml
)
if grep -n -E -- '-o StrictHostKeyChecking=no|-o UserKnownHostsFile=/dev/null|ssh-keyscan|sqlcmd|--apply-branch-migrations|--apply-central-migrations|systemctl (restart|stop|disable) xtrapos-api' "${scope[@]}"; then
  fail "staging deploy files contain a forbidden production or migration command"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat >"$tmp/ssh" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >>"${SSH_LOG:?}"
exit 97
EOF
cat >"$tmp/scp" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >>"${SSH_LOG:?}"
exit 97
EOF
chmod 755 "$tmp/ssh" "$tmp/scp"

export STAGING_SSH_HOST_SECRET="69.197.178.239"
export STAGING_SSH_USER_SECRET="xtrapos-deploy"

run_activate() {
  CONFIRM=staging GIT_SHA="$(printf 'a%.0s' {1..40})" \
    STAGING_SSH_HOST_SECRET="$STAGING_SSH_HOST_SECRET" \
    STAGING_SSH_USER_SECRET="$STAGING_SSH_USER_SECRET" \
    SSH_LOG="$tmp/called" SSH_BIN="$tmp/ssh" SCP_BIN="$tmp/scp" \
    bash deploy/staging-remote-activate.sh deploy >"$tmp/out" 2>"$tmp/err" || true
}

: >"$tmp/called"
CONFIRM=staging GIT_SHA="$(printf 'a%.0s' {1..40})" bash deploy/staging-remote-activate.sh deploy >"$tmp/out" 2>"$tmp/err" || code=$?
[[ "${code:-0}" -ne 0 ]] || fail "deploy without a key should fail"
[[ ! -s "$tmp/called" ]] || fail "ssh was invoked without a key"

export STAGING_SSH_KEY="$(printf '%s\n' '-----BEGIN OPENSSH PRIVATE KEY-----' 'dGVzdA==' '-----END OPENSSH PRIVATE KEY-----')"
export STAGING_KNOWN_HOSTS='* ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeKeyMaterialForTestOnly'
run_activate
[[ ! -s "$tmp/called" ]] || fail "wildcard known_hosts reached ssh"
grep -q 'wildcard' "$tmp/err" || fail "wildcard known_hosts was not rejected"

export STAGING_KNOWN_HOSTS='10.0.0.8 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeKeyMaterialForTestOnly'
: >"$tmp/called"
run_activate
[[ ! -s "$tmp/called" ]] || fail "wrong host reached ssh"

export STAGING_KNOWN_HOSTS='69.197.178.239 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeKeyMaterialForTestOnly'
export RELEASE_DIR="$tmp/release"
mkdir -p "$RELEASE_DIR/web" "$RELEASE_DIR/api"
sha="$(printf 'b%.0s' {1..40})"
python3 - "$sha" "$RELEASE_DIR" <<'PY'
import json, pathlib, sys
sha, dest = sys.argv[1:]
text = json.dumps({"version": "20261031-000000-bbbbbbb", "sha": sha, "createdAt": "2026-10-31T00:00:00Z", "migrations": "not-applied"}) + "\n"
pathlib.Path(dest, "release.json").write_text(text)
pathlib.Path(dest, "web", "release.json").write_text(text)
PY
: >"$tmp/called"
CONFIRM=staging GIT_SHA="$sha" SSH_LOG="$tmp/called" SSH_BIN="$tmp/ssh" SCP_BIN="$tmp/scp" \
  bash deploy/staging-remote-activate.sh deploy >"$tmp/out" 2>"$tmp/err" || true
[[ -s "$tmp/called" ]] || fail "a pinned host key did not reach the ssh client"
grep -q 'StrictHostKeyChecking=yes' "$tmp/called" || fail "ssh was not pinned to StrictHostKeyChecking=yes"
grep -q 'xtrapos-deploy@69.197.178.239' "$tmp/called" || fail "ssh target was not the staging account"
if grep -q -E '5216|xtrapos-api|StrictHostKeyChecking=no' "$tmp/called"; then
  fail "ssh command line included a production target"
fi

: >"$tmp/called"
STAGING_SSH_USER_SECRET=root CONFIRM=staging GIT_SHA="$(printf 'c%.0s' {1..40})" \
  STAGING_SSH_KEY="$STAGING_SSH_KEY" STAGING_KNOWN_HOSTS="$STAGING_KNOWN_HOSTS" \
  SSH_LOG="$tmp/called" SSH_BIN="$tmp/ssh" SCP_BIN="$tmp/scp" \
  bash deploy/staging-remote-activate.sh inspect >"$tmp/out" 2>"$tmp/err" || true
[[ ! -s "$tmp/called" ]] || fail "a non-deploy SSH account reached ssh"
grep -q 'not the staging deploy account' "$tmp/err" || fail "wrong SSH account was not rejected"

CONFIRM=nope GIT_SHA=dead bash deploy/staging-preflight.sh >"$tmp/out" 2>"$tmp/err" || code=$?
[[ "${code:-0}" -ne 0 ]] || fail "preflight accepted an empty request"

if grep -n -E 'mv[[:space:]]+.*App_Data' deploy/staging-on-server.sh; then
  fail "App_Data is moved off the previous release"
fi
if grep -n -E 'STAGING_TEST_ROOT|STAGING_SOURCE_ONLY|STAGING_NGINX_DUMP|SYSTEMCTL_BIN|STAGING_DB_NAME_BIN' .github/workflows/deploy-staging.yml .github/workflows/ci.yml; then
  fail "workflow enables the staging test hook"
fi

app_root="$(mktemp -d /tmp/xtrapos-appdata.XXXXXX)"
outside="$(mktemp -d /tmp/xtrapos-outside.XXXXXX)"
empty="$(mktemp -d /tmp/xtrapos-ignored.XXXXXX)"
trap 'rm -rf "$tmp" "$app_root" "$outside" "$empty"' EXIT
old_api="$app_root/releases/old/api"
new_api="$app_root/releases/new/api"
mkdir -p "$old_api/App_Data" "$new_api/App_Data"
printf 'keep\n' >"$old_api/App_Data/keep.txt"
printf 'fresh\n' >"$new_api/App_Data/fresh.txt"
(
  export STAGING_SOURCE_ONLY=yes
  export STAGING_TEST_ROOT="$app_root"
  # shellcheck disable=SC1091
  source deploy/staging-on-server.sh
  preserve_app_data "$new_api" "$old_api"
)
[[ -f "$old_api/App_Data/keep.txt" ]] || fail "previous App_Data was removed"
[[ ! -L "$old_api/App_Data" && -d "$old_api/App_Data" ]] || fail "previous App_Data is no longer a real directory"
[[ -L "$new_api/App_Data" ]] || fail "new App_Data is not a symlink"
[[ "$(readlink -f "$new_api/App_Data")" == "$(readlink -f "$app_root/shared/app-data")" ]] || fail "new App_Data does not point at shared storage"
[[ -f "$app_root/shared/app-data/keep.txt" ]] || fail "shared App_Data copy is missing"
[[ ! -e "$app_root/shared/app-data/fresh.txt" ]] || fail "new release App_Data replaced the persisted copy"
printf 'later\n' >"$old_api/App_Data/later.txt"
(
  export STAGING_SOURCE_ONLY=yes
  export STAGING_TEST_ROOT="$app_root"
  # shellcheck disable=SC1091
  source deploy/staging-on-server.sh
  preserve_app_data "$new_api" "$old_api"
)
[[ -f "$old_api/App_Data/keep.txt" && -f "$old_api/App_Data/later.txt" ]] || fail "a second persistence pass changed the previous release"
[[ ! -e "$app_root/shared/app-data/later.txt" ]] || fail "existing shared App_Data was rebuilt from the live release"

printf 'secret\n' >"$outside/secret.txt"
rm -rf "$old_api/App_Data" "$new_api/App_Data" "$app_root/shared"
ln -s "$outside" "$old_api/App_Data"
(
  export STAGING_SOURCE_ONLY=yes
  export STAGING_TEST_ROOT="$app_root"
  # shellcheck disable=SC1091
  source deploy/staging-on-server.sh
  preserve_app_data "$new_api" "$old_api"
)
[[ -L "$old_api/App_Data" && "$(readlink "$old_api/App_Data")" == "$outside" ]] || fail "previous App_Data symlink was replaced"
[[ ! -e "$app_root/shared/app-data/secret.txt" ]] || fail "App_Data outside the staging root was copied"
[[ -L "$new_api/App_Data" && -d "$app_root/shared/app-data" ]] || fail "new release was not linked to shared App_Data"

code=0
STAGING_TEST_ROOT="$empty" bash deploy/staging-on-server.sh >"$tmp/out" 2>"$tmp/err" || code=$?
[[ "$code" -ne 0 ]] || fail "server script ran without a sha"
grep -q 'REMOTE_ERROR sha' "$tmp/err" || fail "server script did not reject a missing sha"
[[ ! -e "$empty/shared" ]] || fail "a normal run used the test root"

cat >"$tmp/ssh-ok" <<'EOF'
#!/bin/sh
cat >/dev/null
printf '%s\n' "$*" >>"${SSH_LOG:?}"
action=""
prev=""
for arg in "$@"; do
  if [ "$prev" = "--" ]; then
    action="$arg"
    break
  fi
  prev="$arg"
done
case "$action" in
  probe)
    printf '%s\n' \
      'PROBE_OK=yes' \
      'POINTER=both' \
      'NGINX_UPSTREAM=http://127.0.0.1:5088' \
      'NGINX_ROOT=/opt/xtrapos-staging/current/web' \
      'DB_NAME=XtraPOS_Branch_Test_Staging'
    ;;
  prepare|activate|rollback)
    printf '%s\n' "OK ${action}"
    ;;
  *)
    printf 'unexpected remote action %s\n' "$action" >&2
    exit 1
    ;;
esac
EOF
cat >"$tmp/scp-ok" <<'EOF'
#!/bin/sh
exit 0
EOF
cat >"$tmp/curl" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >>"${CURL_LOG:?}"
url=""
for arg in "$@"; do
  case "$arg" in
    http://*|https://*) url="$arg" ;;
  esac
done
case "$url" in
  */health)
    if [ "${FAKE_CURL_MODE:?}" = "health-fail" ] || [ "$FAKE_CURL_MODE" = "inspect-fail" ]; then
      exit 22
    fi
    case "$*" in
      *'%{http_code}'*) printf '200' ;;
      *) printf '%s\n' '{"status":"Healthy","version":"0.3.0.0"}' ;;
    esac
    ;;
  */release.json)
    if [ "$FAKE_CURL_MODE" = "sha-fail" ]; then
      printf '%s\n' '{"sha":"deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"}'
    elif [ "$FAKE_CURL_MODE" = "missing-release" ]; then
      exit 22
    else
      printf '%s\n' "{\"sha\":\"${FAKE_SHA:?}\"}"
    fi
    ;;
  */)
    if [ "$FAKE_CURL_MODE" = "page-fail" ]; then
      printf '500'
      exit 22
    fi
    printf '200'
    ;;
  *)
    printf 'unexpected url %s\n' "$url" >&2
    exit 1
    ;;
esac
EOF
chmod 755 "$tmp/ssh-ok" "$tmp/scp-ok" "$tmp/curl"

run_public() {
  local mode="$1" script_action="${2:-deploy}" public_code=0
  : >"$tmp/called"
  : >"$tmp/curl-log"
  set +e
  CONFIRM=staging GIT_SHA="$sha" \
    STAGING_SSH_HOST_SECRET="$STAGING_SSH_HOST_SECRET" \
    STAGING_SSH_USER_SECRET="$STAGING_SSH_USER_SECRET" \
    STAGING_SSH_KEY="$STAGING_SSH_KEY" \
    STAGING_KNOWN_HOSTS="$STAGING_KNOWN_HOSTS" \
    RELEASE_DIR="$RELEASE_DIR" \
    SSH_LOG="$tmp/called" SSH_BIN="$tmp/ssh-ok" SCP_BIN="$tmp/scp-ok" \
    CURL_BIN="$tmp/curl" CURL_LOG="$tmp/curl-log" FAKE_CURL_MODE="$mode" FAKE_SHA="$sha" \
    bash deploy/staging-remote-activate.sh "$script_action" >"$tmp/out" 2>"$tmp/err"
  public_code=$?
  set -e
  return "$public_code"
}

expect_rollback() {
  local mode="$1" code=0
  run_public "$mode" deploy || code=$?
  [[ "$code" -ne 0 ]] || fail "$mode did not fail"
  grep -q 'bash -s -- activate' "$tmp/called" || fail "$mode rolled back before activation"
  grep -q 'bash -s -- rollback' "$tmp/called" || fail "$mode did not restore the previous release"
  grep -q 'previous staging pointer was restored' "$tmp/err" || fail "$mode did not report rollback"
}

expect_rollback health-fail
expect_rollback page-fail
expect_rollback sha-fail
expect_rollback missing-release

code=0
run_public ok deploy || code=$?
[[ "$code" -eq 0 ]] || fail "successful public checks failed"
if grep -q 'bash -s -- rollback' "$tmp/called"; then
  fail "successful public checks restored the previous release"
fi

code=0
run_public inspect-fail inspect || code=$?
[[ "$code" -ne 0 ]] || fail "failed inspect was accepted"
if grep -q 'bash -s -- rollback' "$tmp/called"; then
  fail "inspect restored a release"
fi
grep -q 'bash -s -- probe' "$tmp/called" || fail "inspect did not probe"

cat >"$tmp/nginx-redirect.conf" <<'EOF'
# configuration file /etc/nginx/nginx.conf:
http {
    include /etc/nginx/sites-enabled/*;
}

# configuration file /etc/nginx/sites-enabled/test-pos:
server {
    listen 80;
    listen [::]:80;
    server_name test-pos.sharbelfashion.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl;
    server_name test-pos.sharbelfashion.com;
    ssl_certificate /etc/letsencrypt/live/test-pos.sharbelfashion.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/test-pos.sharbelfashion.com/privkey.pem;
    root /opt/xtrapos-staging/current/web;
    location /health {
        proxy_pass http://127.0.0.1:5088;
    }
    location /api/ {
        proxy_pass http://127.0.0.1:5088/;
    }
}

server {
    listen 443 ssl;
    server_name pos.sharbelfashion.com;
    root /var/www/production;
    location / {
        proxy_pass http://127.0.0.1:5216;
    }
}
EOF
cp "$tmp/nginx-redirect.conf" "$tmp/nginx-redirect-copy.conf"
cat "$tmp/nginx-redirect.conf" >>"$tmp/nginx-redirect-copy.conf"

cat >"$tmp/nginx-https-only.conf" <<'EOF'
server {
    listen 443 ssl http2;
    listen [::]:443 ssl;
    server_name test-pos.sharbelfashion.com;
    root /opt/xtrapos-staging/current/web;
    location /health {
        proxy_pass http://127.0.0.1:5088;
    }
}
EOF

cat >"$tmp/nginx-two-https.conf" <<'EOF'
server {
    listen 80;
    server_name test-pos.sharbelfashion.com;
    return 301 https://$host$request_uri;
}
server {
    listen 443 ssl;
    server_name test-pos.sharbelfashion.com;
    root /opt/xtrapos-staging/current/web;
    location /health {
        proxy_pass http://127.0.0.1:5088;
    }
}
server {
    listen 443 ssl;
    server_name test-pos.sharbelfashion.com;
    root /opt/xtrapos-staging/other/web;
    location /health {
        proxy_pass http://127.0.0.1:5088;
    }
}
EOF

cat >"$tmp/nginx-http-proxy.conf" <<'EOF'
server {
    listen 80;
    server_name test-pos.sharbelfashion.com;
    location / {
        proxy_pass http://127.0.0.1:5088;
    }
}
server {
    listen 443 ssl;
    server_name test-pos.sharbelfashion.com;
    root /opt/xtrapos-staging/current/web;
    location /health {
        proxy_pass http://127.0.0.1:5088;
    }
}
EOF

run_nginx_facts() {
  local dump="$1" nginx_code=0
  set +e
  (
    export STAGING_SOURCE_ONLY=yes
    export STAGING_TEST_ROOT="$app_root"
    export STAGING_NGINX_DUMP="$dump"
    # shellcheck disable=SC1091
    source deploy/staging-on-server.sh
    nginx_facts
  ) >"$tmp/nginx-out" 2>"$tmp/nginx-err"
  nginx_code=$?
  set -e
  return "$nginx_code"
}

nginx_code=0
run_nginx_facts "$tmp/nginx-redirect.conf" || nginx_code=$?
[[ "$nginx_code" -eq 0 ]] || fail "HTTP redirect plus HTTPS server was rejected: $(cat "$tmp/nginx-err")"
grep -qx 'NGINX_ROOT=/opt/xtrapos-staging/current/web' "$tmp/nginx-out" || fail "HTTPS root was not selected"
grep -qx 'NGINX_UPSTREAM=http://127.0.0.1:5088' "$tmp/nginx-out" || fail "HTTPS upstream was not selected"
grep -qx 'NGINX_HTTP_REDIRECT=yes' "$tmp/nginx-out" || fail "HTTP redirect was not recognized"
if grep -q -E '5216|xtrapos-api|NGINX_SERVER_BLOCKS' "$tmp/nginx-out" "$tmp/nginx-err"; then
  fail "staging nginx facts included another server block"
fi

nginx_code=0
run_nginx_facts "$tmp/nginx-redirect-copy.conf" || nginx_code=$?
[[ "$nginx_code" -eq 0 ]] || fail "repeated nginx dump was counted twice: $(cat "$tmp/nginx-err")"
grep -qx 'NGINX_UPSTREAM=http://127.0.0.1:5088' "$tmp/nginx-out" || fail "repeated dump changed the upstream"

nginx_code=0
run_nginx_facts "$tmp/nginx-https-only.conf" || nginx_code=$?
[[ "$nginx_code" -eq 0 ]] || fail "HTTPS-only server was rejected: $(cat "$tmp/nginx-err")"
grep -qx 'NGINX_HTTP_REDIRECT=no' "$tmp/nginx-out" || fail "HTTPS-only server reported a redirect"

nginx_code=0
run_nginx_facts "$tmp/nginx-two-https.conf" || nginx_code=$?
[[ "$nginx_code" -ne 0 ]] || fail "two HTTPS server blocks were accepted"
grep -q 'NGINX_SERVER_BLOCKS 2' "$tmp/nginx-err" || fail "two HTTPS server blocks did not keep the block count"

nginx_code=0
run_nginx_facts "$tmp/nginx-http-proxy.conf" || nginx_code=$?
[[ "$nginx_code" -ne 0 ]] || fail "an HTTP proxy was accepted as a redirect"
grep -q 'NGINX_HTTP_PROXY' "$tmp/nginx-err" || fail "HTTP proxy was not rejected"

nginx_code=0
STAGING_SOURCE_ONLY=yes STAGING_TEST_ROOT="$app_root" STAGING_NGINX_DUMP="/etc/nginx/nginx.conf" \
  bash -c 'source deploy/staging-on-server.sh; nginx_facts' >"$tmp/nginx-out" 2>"$tmp/nginx-err" || nginx_code=$?
[[ "$nginx_code" -ne 0 ]] || fail "nginx dump outside the test root was read"
grep -q 'REMOTE_ERROR nginx dump' "$tmp/nginx-err" || fail "nginx dump outside the test root was not rejected"
[[ ! -s "$tmp/nginx-out" ]] || fail "rejected nginx dump produced facts"

db_secret="db-password-should-not-print"
mkdir -p "$tmp/api" "$tmp/empty-work" "$tmp/api-other" "$tmp/api-match"
cat >"$tmp/plain.env" <<EOF
# staging database name only
export SqlServer__Database="XtraPOS_Branch_Test_Staging"
SqlServer__Password=${db_secret}
EOF
printf 'OTHER=1\n' >"$tmp/other.env"
printf 'SqlServer__Database=XtraPOS_Branch_Test_Other\nSqlServer__Password=%s\n' "$db_secret" >"$tmp/other-db.env"
printf 'Database=XtraPOS_Branch_Test_Staging\n' >"$tmp/wrong-key.env"
printf 'SqlServer__Database=XtraPOS_Central\nSqlServer__Password=%s\n' "$db_secret" >"$tmp/operational.env"
mkdir -p "$tmp/not-a-file"
printf 'SqlServer__Database=XtraPOS_Branch_Test_Hidden\nSqlServer__Password=%s\n' "$db_secret" >"$tmp/denied.env"
chmod 000 "$tmp/denied.env"
cat >"$tmp/api/appsettings.Staging.json" <<EOF
{"SqlServer":{"Database":"XtraPOS_Branch_Test_Staging","Password":"${db_secret}"}}
EOF
cat >"$tmp/api-other/appsettings.Staging.json" <<EOF
{"SqlServer":{"Database":"XtraPOS_Branch_Test_AppOnly","Password":"${db_secret}"}}
EOF
cp "$tmp/api/appsettings.Staging.json" "$tmp/api-match/appsettings.Staging.json"
cat >"$tmp/db-helper" <<'EOF'
#!/bin/sh
printf '%s\n' 'DB_NAME=XtraPOS_Branch_Test_Staging'
EOF
cat >"$tmp/db-helper-other" <<'EOF'
#!/bin/sh
printf '%s\n' 'DB_NAME=XtraPOS_Branch_Test_Other'
EOF
cat >"$tmp/db-helper-central" <<'EOF'
#!/bin/sh
printf '%s\n' 'DB_NAME=XtraPOS_Central'
EOF
cat >"$tmp/db-helper-leaky" <<EOF
#!/bin/sh
printf '%s\n' 'DB_NAME=XtraPOS_Branch_Test_Staging' 'SqlServer__Password=${db_secret}'
EOF
chmod 755 "$tmp/db-helper" "$tmp/db-helper-other" "$tmp/db-helper-central" "$tmp/db-helper-leaky"

run_db_read() {
  local files="$1" work="$2" mode="${3:-load}" helper="${4:-}"
  (
    export STAGING_SOURCE_ONLY=yes
    export STAGING_TEST_ROOT="$app_root"
    unset SYSTEMCTL_BIN
    if [[ -n "$helper" ]]; then
      export STAGING_DB_NAME_BIN="$helper"
    else
      unset STAGING_DB_NAME_BIN
    fi
    # shellcheck disable=SC1091
    source deploy/staging-on-server.sh
    if [[ "$mode" == "resolve" ]]; then
      resolve_staging_database "$files" "$work"
    else
      load_staging_database "$files" "$work"
    fi
  )
}

assert_no_secret() {
  if printf '%s\n' "$1" | grep -q "$db_secret"; then
    fail "$2"
  fi
}

db_info="$(run_db_read "$tmp/other.env (ignore_errors=no) $tmp/plain.env (ignore_errors=yes)" "")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME=XtraPOS_Branch_Test_Staging' || fail "quoted environment file did not provide the staging database name"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=environment-file' || fail "database name was not read from the environment file"
printf '%s\n' "$db_info" | grep -qx "DB_FILE=$tmp/plain.env" || fail "database name was read from the wrong environment file"
printf '%s\n' "$db_info" | grep -qx 'DB_FILE_READABLE=yes' || fail "readable environment file was reported as unreadable"
assert_no_secret "$db_info" "environment file read printed a secret"

db_info="$(run_db_read "" "$tmp/api")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME=XtraPOS_Branch_Test_Staging' || fail "appsettings database name was ignored when no environment file is configured"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=appsettings' || fail "appsettings was not identified as the database source"
printf '%s\n' "$db_info" | grep -qx "DB_FILE=$tmp/api/appsettings.Staging.json" || fail "appsettings path was not reported"
assert_no_secret "$db_info" "appsettings read printed a secret"

db_info="$(run_db_read "$tmp/other.env (ignore_errors=no)" "$tmp/api")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=appsettings' || fail "appsettings was ignored when the environment file does not set the database"
assert_no_secret "$db_info" "environment file without the database key printed a secret"

db_code=0
run_db_read "$tmp/denied.env (ignore_errors=no)" "$tmp/api-other" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a permission-denied environment file was accepted via appsettings"
grep -q 'staging database name is unreadable (permissions)' "$tmp/db-err" || fail "a permission-denied environment file was not reported as permissions"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_AppOnly|XtraPOS_Branch_Test_Hidden' "$tmp/db-out" "$tmp/db-err"; then
  fail "a permission-denied environment file selected an unverified database name"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "a permission-denied environment file printed a secret"
fi

db_code=0
run_db_read "$tmp/plain.env (ignore_errors=no) $tmp/denied.env (ignore_errors=yes)" "$tmp/api-match" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a readable sibling file bypassed an unreadable environment file"
grep -q 'staging database name is unreadable (permissions)' "$tmp/db-err" || fail "a mixed readable and unreadable environment file was not fail-closed"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_' "$tmp/db-out" "$tmp/db-err"; then
  fail "a mixed environment file list selected a database without reading every file"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "a mixed environment file list printed a secret"
fi

db_info="$(run_db_read "$tmp/denied.env (ignore_errors=no)" "" load "$tmp/db-helper")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME=XtraPOS_Branch_Test_Staging' || fail "the database-name helper was not accepted for an unreadable file"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=db-name-helper' || fail "the database-name helper was not identified as the source"
printf '%s\n' "$db_info" | grep -qx 'DB_FILE_READABLE=no' || fail "the helper path was reported as a direct file read"
assert_no_secret "$db_info" "the database-name helper printed a secret"

db_code=0
run_db_read "$tmp/denied.env (ignore_errors=no)" "$tmp/api-other" load "$tmp/db-helper" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a helper and appsettings disagreement was accepted"
grep -q 'staging database name conflicts' "$tmp/db-err" || fail "a helper and appsettings disagreement was not a conflict"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_Staging|XtraPOS_Branch_Test_AppOnly' "$tmp/db-out" "$tmp/db-err"; then
  fail "a helper and appsettings conflict selected a database"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "a helper and appsettings conflict printed a secret"
fi

db_code=0
run_db_read "$tmp/plain.env (ignore_errors=no) $tmp/denied.env (ignore_errors=no)" "" load "$tmp/db-helper-other" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a helper and environment-file disagreement was accepted"
grep -q 'staging database name conflicts' "$tmp/db-err" || fail "a helper and environment-file disagreement was not a conflict"
if grep -q -E 'DB_NAME=' "$tmp/db-out"; then
  fail "a helper and environment-file conflict selected a database"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "a helper and environment-file conflict printed a secret"
fi

db_code=0
run_db_read "$tmp/plain.env (ignore_errors=no) $tmp/other-db.env (ignore_errors=yes)" "" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "two environment files with different database names were accepted"
grep -q 'staging database name conflicts' "$tmp/db-err" || fail "different environment file database names were not a conflict"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_Staging|XtraPOS_Branch_Test_Other' "$tmp/db-out" "$tmp/db-err"; then
  fail "conflicting environment files selected a database"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "conflicting environment files printed a secret"
fi

db_code=0
run_db_read "$tmp/plain.env (ignore_errors=no)" "$tmp/api-other" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "an environment file and appsettings disagreement was accepted"
grep -q 'staging database name conflicts' "$tmp/db-err" || fail "an environment file and appsettings disagreement was not a conflict"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_Staging|XtraPOS_Branch_Test_AppOnly' "$tmp/db-out" "$tmp/db-err"; then
  fail "an environment file and appsettings conflict selected a database"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "an environment file and appsettings conflict printed a secret"
fi

db_code=0
run_db_read "$tmp/denied.env (ignore_errors=no)" "" load "$tmp/db-helper-leaky" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a helper that printed more than the database name was accepted"
grep -q 'staging database name is unreadable (permissions)' "$tmp/db-err" || fail "a noisy helper was not rejected"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_Staging' "$tmp/db-out" "$tmp/db-err"; then
  fail "a noisy helper selected a database"
fi
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "a noisy helper printed a secret"
fi

db_code=0
run_db_read "$tmp/denied.env (ignore_errors=no)" "" load "/etc/passwd" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a helper path outside the test root was executed"
if grep -q '^root:' "$tmp/db-out" "$tmp/db-err"; then
  fail "a helper path outside the test root was read"
fi

db_code=0
run_db_read "/etc/passwd (ignore_errors=no)" "" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a non-staging environment file was read"
grep -q 'staging database name is unreadable (path)' "$tmp/db-err" || fail "a non-staging environment file was not rejected"
if grep -q '^root:' "$tmp/db-out" "$tmp/db-err"; then
  fail "inspection read a system account file"
fi

db_code=0
run_db_read "/tmp/../etc/passwd (ignore_errors=no)" "" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "an environment file path with .. was read"
if grep -q '^root:' "$tmp/db-out" "$tmp/db-err"; then
  fail "an environment file path with .. read a system account file"
fi

db_code=0
run_db_read "$tmp/not-a-file (ignore_errors=no)" "$tmp/api-other" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a non-regular environment file was accepted via appsettings"
grep -q 'staging database name is unreadable (permissions)' "$tmp/db-err" || fail "a non-regular environment file was not fail-closed"
if grep -q 'XtraPOS_Branch_Test_AppOnly' "$tmp/db-out" "$tmp/db-err"; then
  fail "a non-regular environment file selected appsettings"
fi

db_code=0
run_db_read "$tmp/operational.env (ignore_errors=no)" "" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "an operational database from the environment file was accepted"
grep -q 'refusing operational database' "$tmp/db-err" || fail "operational database was not refused"
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "operational database refusal printed a secret"
fi

db_code=0
run_db_read "$tmp/denied.env (ignore_errors=no)" "" load "$tmp/db-helper-central" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "an operational database from the helper was accepted"
grep -q 'refusing operational database' "$tmp/db-err" || fail "an operational database from the helper was not refused"
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "operational helper refusal printed a secret"
fi

db_code=0
run_db_read "$tmp/wrong-key.env (ignore_errors=no)" "$tmp/empty-work" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a different variable name was accepted as the database"
grep -q 'staging database name is unreadable (name)' "$tmp/db-err" || fail "a missing SqlServer__Database key was not reported as the variable name"

cat >"$tmp/systemctl-db-name" <<EOF
#!/bin/sh
case "\$*" in
  "show xtrapos-staging.service -p EnvironmentFiles --value --no-pager")
    printf '%s\n' "$tmp/plain.env (ignore_errors=no)"
    ;;
  *)
    exit 1
    ;;
esac
EOF
cat >"$tmp/systemctl-db-conflict" <<EOF
#!/bin/sh
case "\$*" in
  "show xtrapos-staging.service -p EnvironmentFiles --value --no-pager")
    printf '%s\n' "$tmp/plain.env (ignore_errors=no)" "$tmp/other-db.env (ignore_errors=yes)"
    ;;
  *)
    exit 1
    ;;
esac
EOF
cat >"$tmp/systemctl-db-denied" <<EOF
#!/bin/sh
case "\$*" in
  "show xtrapos-staging.service -p EnvironmentFiles --value --no-pager")
    printf '%s\n' "$tmp/denied.env (ignore_errors=no)"
    ;;
  *)
    exit 1
    ;;
esac
EOF
cat >"$tmp/systemctl-db-passwd" <<'EOF'
#!/bin/sh
case "$*" in
  "show xtrapos-staging.service -p EnvironmentFiles --value --no-pager")
    printf '%s\n' "/etc/passwd (ignore_errors=no)"
    ;;
  *)
    exit 1
    ;;
esac
EOF
chmod 755 "$tmp/systemctl-db-name" "$tmp/systemctl-db-conflict" "$tmp/systemctl-db-denied" "$tmp/systemctl-db-passwd"

STAGING_SOURCE_ONLY=yes SYSTEMCTL_BIN="$tmp/systemctl-db-name" \
  bash deploy/staging-db-name.sh >"$tmp/db-out" 2>"$tmp/db-err"
[[ "$(cat "$tmp/db-out")" == "DB_NAME=XtraPOS_Branch_Test_Staging" ]] || fail "the database-name helper did not print only the database name"
[[ ! -s "$tmp/db-err" ]] || fail "the database-name helper wrote extra output"
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "the database-name helper printed a secret"
fi

db_code=0
STAGING_SOURCE_ONLY=yes SYSTEMCTL_BIN="$tmp/systemctl-db-conflict" \
  bash deploy/staging-db-name.sh >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -eq 1 ]] || fail "the database-name helper accepted conflicting files"
grep -qx 'DB_VERIFIER_CONFLICT' "$tmp/db-err" || fail "the database-name helper did not report a conflict"
if grep -q -E 'DB_NAME=|XtraPOS_Branch_Test_|'"$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "the database-name helper printed a database or secret during a conflict"
fi

db_code=0
STAGING_SOURCE_ONLY=yes SYSTEMCTL_BIN="$tmp/systemctl-db-denied" \
  bash deploy/staging-db-name.sh >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -eq 1 ]] || fail "the database-name helper read a permission-denied file"
grep -qx 'DB_VERIFIER_PERMISSIONS' "$tmp/db-err" || fail "the database-name helper did not report permissions"
if grep -q -E 'XtraPOS_Branch_Test_|'"$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "the database-name helper printed a database or secret after a permission failure"
fi

db_code=0
STAGING_SOURCE_ONLY=yes SYSTEMCTL_BIN="$tmp/systemctl-db-passwd" \
  bash deploy/staging-db-name.sh >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -eq 1 ]] || fail "the database-name helper accepted a system account file"
grep -qx 'DB_VERIFIER_PATH' "$tmp/db-err" || fail "the database-name helper did not reject a system account file"
if grep -q '^root:' "$tmp/db-out" "$tmp/db-err"; then
  fail "the database-name helper read a system account file"
fi

db_code=0
bash deploy/staging-db-name.sh "$tmp/plain.env" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -eq 2 ]] || fail "the database-name helper accepted an argument"
grep -qx 'DB_VERIFIER_ARGS' "$tmp/db-err" || fail "the database-name helper did not reject an argument"
if grep -q -E 'XtraPOS_Branch_Test_|'"$db_secret"'|^root:' "$tmp/db-out" "$tmp/db-err"; then
  fail "the database-name helper printed a value when given an argument"
fi

chmod 600 "$tmp/denied.env" 2>/dev/null || true
echo "Staging safety checks passed"
