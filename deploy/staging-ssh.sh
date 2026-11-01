#!/usr/bin/env bash
# OpenSSH wrapper for the staging host only.
# Host-key verification stays on. This file does not accept a different host, user, or key file.
set -euo pipefail

STAGING_SSH_HOST="69.197.178.239"
STAGING_SSH_USER="xtrapos-deploy"
STAGING_SSH_TARGET="${STAGING_SSH_USER}@${STAGING_SSH_HOST}"
STAGING_ROOT="/opt/xtrapos-staging"
STAGING_SERVICE="xtrapos-staging.service"
STAGING_API_PORT="5088"
FORBIDDEN_SERVICE="xtrapos-api.service"
FORBIDDEN_PORT="5216"
STAGING_PUBLIC_URL="https://test-pos.sharbelfashion.com"

staging_die() {
  echo "::error::$1" >&2
  exit 2
}

staging_prepare_ssh() {
  [[ "${CONFIRM:-}" == "staging" ]] || staging_die "confirm must be staging"
  [[ "${GIT_SHA:-}" =~ ^[0-9a-f]{40}$ ]] || staging_die "GIT_SHA must be the full commit SHA"
  [[ -n "${STAGING_SSH_KEY:-}" ]] || staging_die "STAGING_SSH_KEY is missing"
  [[ -n "${STAGING_KNOWN_HOSTS:-}" ]] || staging_die "STAGING_KNOWN_HOSTS is missing"
  [[ "${STAGING_SSH_HOST_SECRET:-}" == "$STAGING_SSH_HOST" ]] || staging_die "STAGING_SSH_HOST is not the staging server"
  [[ "${STAGING_SSH_USER_SECRET:-}" == "$STAGING_SSH_USER" ]] || staging_die "STAGING_SSH_USER is not the staging deploy account"

  local dir key hosts line
  dir="${TMPDIR:-/tmp}/xtrapos-staging-ssh.$$"
  mkdir -p "$dir"
  chmod 700 "$dir"
  key="$dir/id"
  hosts="$dir/known_hosts"
  printf '%s' "$STAGING_SSH_KEY" >"$key"
  if [[ -n "$(tail -c 1 "$key")" ]]; then
    printf '\n' >>"$key"
  fi
  printf '%s' "$STAGING_KNOWN_HOSTS" >"$hosts"
  if [[ -n "$(tail -c 1 "$hosts")" ]]; then
    printf '\n' >>"$hosts"
  fi
  chmod 600 "$key" "$hosts"
  grep -q 'PRIVATE KEY' "$key" || staging_die "STAGING_SSH_KEY is not a private key"
  if grep -q -E 'StrictHostKeyChecking[[:space:]]*no|UserKnownHostsFile=/dev/null|\*' "$hosts"; then
    staging_die "STAGING_KNOWN_HOSTS disables host-key verification or uses a wildcard"
  fi
  line="$(grep -E "^(\[)?${STAGING_SSH_HOST}(\])?(:22)?[[:space:]]" "$hosts" || true)"
  [[ -n "$line" ]] || staging_die "STAGING_KNOWN_HOSTS has no entry for ${STAGING_SSH_HOST}"
  if ! grep -q -E 'ssh-ed25519|ssh-rsa|ecdsa-sha2-|ssh-dss' <<<"$line"; then
    staging_die "STAGING_KNOWN_HOSTS entry for ${STAGING_SSH_HOST} has no key type"
  fi

  STAGING_KEY_FILE="$key"
  STAGING_KNOWN_HOSTS_FILE="$hosts"
  SSH_BIN="${SSH_BIN:-/usr/bin/ssh}"
  SCP_BIN="${SCP_BIN:-/usr/bin/scp}"
  [[ -x "$SSH_BIN" ]] || staging_die "ssh client is missing"
  [[ -x "$SCP_BIN" ]] || staging_die "scp client is missing"
}

staging_ssh_options=(
  -o BatchMode=yes
  -o StrictHostKeyChecking=yes
  -o IdentitiesOnly=yes
  -o ConnectTimeout=20
)

staging_ssh() {
  "$SSH_BIN" \
    "${staging_ssh_options[@]}" \
    -o "UserKnownHostsFile=${STAGING_KNOWN_HOSTS_FILE}" \
    -i "$STAGING_KEY_FILE" \
    "$STAGING_SSH_TARGET" \
    "$@"
}

staging_scp() {
  "$SCP_BIN" \
    "${staging_ssh_options[@]}" \
    -o "UserKnownHostsFile=${STAGING_KNOWN_HOSTS_FILE}" \
    -i "$STAGING_KEY_FILE" \
    "$@"
}
