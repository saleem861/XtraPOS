#!/usr/bin/env bash
# Non-deployment checks for the staging scripts. This does not open a network connection.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "::error::$1" >&2
  exit 1
}

for file in deploy/staging-ssh.sh deploy/staging-on-server.sh deploy/staging-remote-activate.sh deploy/staging-rollback.sh deploy/staging-preflight.sh deploy/scan-release.sh; do
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
if grep -n -E 'STAGING_TEST_ROOT|STAGING_SOURCE_ONLY|STAGING_NGINX_DUMP|SYSTEMCTL_BIN' .github/workflows/deploy-staging.yml .github/workflows/ci.yml; then
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
mkdir -p "$tmp/api"
cat >"$tmp/plain.env" <<EOF
# staging database name only
export SqlServer__Database="XtraPOS_Branch_Test_Staging"
SqlServer__Password=${db_secret}
EOF
printf 'OTHER=1\n' >"$tmp/other.env"
printf 'Database=XtraPOS_Branch_Test_Staging\n' >"$tmp/wrong-key.env"
mkdir -p "$tmp/not-a-file"
cat >"$tmp/api/appsettings.Staging.json" <<EOF
{"SqlServer":{"Database":"XtraPOS_Branch_Test_Staging","Password":"${db_secret}"}}
EOF
cat >"$tmp/systemctl-db" <<EOF
#!/bin/sh
case "\$*" in
  *"-p Environment "*)
    printf '%s\n' "{\"Environment\":[\"SqlServer__Password=${db_secret}\",\"SqlServer__Database=\${FAKE_DB_NAME}\"]}"
    ;;
  *)
    exit 0
    ;;
esac
EOF
chmod 755 "$tmp/systemctl-db"

run_db_read() {
  local files="$1" work="$2" mode="${3:-load}"
  (
    export STAGING_SOURCE_ONLY=yes
    export STAGING_TEST_ROOT="$app_root"
    export SYSTEMCTL_BIN="$tmp/systemctl-db"
    export FAKE_DB_NAME="${FAKE_DB_NAME-XtraPOS_Branch_Test_Staging}"
    # shellcheck disable=SC1091
    source deploy/staging-on-server.sh
    if [[ "$mode" == "resolve" ]]; then
      resolve_staging_database "$files" "$work"
    else
      load_staging_database "$files" "$work"
    fi
  )
}

FAKE_DB_NAME="XtraPOS_Branch_Test_Staging"
db_info="$(run_db_read "$tmp/other.env (ignore_errors=no) $tmp/plain.env (ignore_errors=yes)" "")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME=XtraPOS_Branch_Test_Staging' || fail "quoted environment file did not provide the staging database name"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=environment-file' || fail "database name was not read from the environment file"
printf '%s\n' "$db_info" | grep -qx "DB_FILE=$tmp/plain.env" || fail "database name was read from the wrong environment file"
if printf '%s\n' "$db_info" | grep -q "$db_secret"; then
  fail "environment file read printed a secret"
fi

db_info="$(run_db_read "$tmp/not-a-file (ignore_errors=no)" "")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME=XtraPOS_Branch_Test_Staging' || fail "unreadable environment file did not use the unit assignment"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=unit-environment' || fail "unit environment was not used when the file was unreadable"
printf '%s\n' "$db_info" | grep -qx 'DB_FILE_READABLE=no' || fail "unreadable environment file was reported as readable"
if printf '%s\n' "$db_info" | grep -q "$db_secret"; then
  fail "unit environment read printed a secret"
fi

db_info="$(run_db_read "/etc/passwd (ignore_errors=no)" "")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=unit-environment' || fail "a non-staging environment file was read"
if printf '%s\n' "$db_info" | grep -q '^root:'; then
  fail "inspection read a system account file"
fi

FAKE_DB_NAME=""
db_info="$(run_db_read "" "$tmp/api")"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME=XtraPOS_Branch_Test_Staging' || fail "appsettings database name was ignored"
printf '%s\n' "$db_info" | grep -qx 'DB_NAME_SOURCE=appsettings' || fail "appsettings was not identified as the database source"
printf '%s\n' "$db_info" | grep -qx "DB_FILE=$tmp/api/appsettings.Staging.json" || fail "appsettings path was not reported"
if printf '%s\n' "$db_info" | grep -q "$db_secret"; then
  fail "appsettings read printed a secret"
fi

FAKE_DB_NAME="XtraPOS_Central"
db_code=0
run_db_read "$tmp/not-a-file (ignore_errors=no)" "" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "an operational database from the unit environment was accepted"
grep -q 'refusing operational database' "$tmp/db-err" || fail "operational database was not refused"
if grep -q "$db_secret" "$tmp/db-out" "$tmp/db-err"; then
  fail "operational database refusal printed a secret"
fi

FAKE_DB_NAME=""
db_code=0
run_db_read "$tmp/wrong-key.env (ignore_errors=no)" "$tmp/empty-work" >"$tmp/db-out" 2>"$tmp/db-err" || db_code=$?
[[ "$db_code" -ne 0 ]] || fail "a different variable name was accepted as the database"
grep -q 'staging database name is unreadable (name)' "$tmp/db-err" || fail "a missing SqlServer__Database key was not reported as the variable name"

echo "Staging safety checks passed"
