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

echo "Staging safety checks passed"
