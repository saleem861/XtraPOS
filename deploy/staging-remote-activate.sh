#!/usr/bin/env bash
# Deploy or roll back the staging host. Does nothing until confirm, the SHA,
# the private key, and a pinned known_hosts entry are present.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=staging-ssh.sh
source "$script_dir/staging-ssh.sh"
CURL_BIN="${CURL_BIN:-curl}"

action="${1:-}"
[[ "$action" == "deploy" || "$action" == "rollback" || "$action" == "inspect" ]] || staging_die "action must be inspect, deploy, or rollback"
staging_prepare_ssh

release_dir="${RELEASE_DIR:-}"
if [[ "$action" == "deploy" ]]; then
  [[ -d "$release_dir" ]] || staging_die "RELEASE_DIR is missing"
  bash "$script_dir/scan-release.sh" "$release_dir"
fi

remote() {
  staging_ssh bash -s -- "$@" <"$script_dir/staging-on-server.sh"
}

public_verification_ok() {
  local health page manifest actual
  health="$("$CURL_BIN" -fsS --max-time 20 "${STAGING_PUBLIC_URL}/health" 2>/dev/null || true)"
  page="$("$CURL_BIN" -sS -o /dev/null -w '%{http_code}' --max-time 20 "${STAGING_PUBLIC_URL}/" 2>/dev/null || true)"
  manifest="$("$CURL_BIN" -fsS --max-time 20 "${STAGING_PUBLIC_URL}/release.json" 2>/dev/null || true)"
  actual=""
  if [[ -n "$manifest" ]]; then
    actual="$(python3 -c 'import json,sys
try:
    print(json.loads(sys.argv[1]).get("sha", ""))
except Exception:
    pass' "$manifest" 2>/dev/null || true)"
  fi
  [[ "$health" == *'"Healthy"'* && "$page" == "200" ]] || return 1
  if [[ "$action" == "deploy" && "$actual" != "$GIT_SHA" ]]; then
    return 1
  fi
  return 0
}

probe_output="$(remote probe "$GIT_SHA" none none)"
grep -q '^PROBE_OK=yes$' <<<"$probe_output" || staging_die "staging probe failed"
grep -E -q '^NGINX_UPSTREAM=.*:5088(/|$)' <<<"$probe_output" || staging_die "nginx upstream is not port 5088"
grep -E -q '^NGINX_ROOT=/opt/xtrapos-staging/' <<<"$probe_output" || staging_die "nginx root is outside the staging root"
grep -q '^DB_NAME=' <<<"$probe_output" || staging_die "database name was not confirmed"
if grep -E '^DB_NAME=(XtraPOS_Central|XtraPOS_Branch|XtraPOS_Branch_01|XtraPOS_Branch_02|saleemPOSDB|Shop1|Shop2|swstore)$' <<<"$probe_output"; then
  staging_die "refusing an operational database"
fi

if [[ "$action" == "inspect" ]]; then
  printf '%s\n' "$probe_output" | awk -F= '$1 ~ /^(PROBE_OK|POINTER|CURRENT_ID|CURRENT_TARGET|WORKING_DIRECTORY|STAGING_LISTEN|PRODUCTION_PORT|ENV_FILE|DB_NAME|DB_NAME_SOURCE|DB_FILE_READABLE|KEY_DIR|ROOT_OWNER|NGINX_ROOT|NGINX_UPSTREAM|NGINX_HTTP_REDIRECT)$/'
  health_code="$("$CURL_BIN" -fsS -o /dev/null -w '%{http_code}' --max-time 20 "${STAGING_PUBLIC_URL}/health" || true)"
  page_code="$("$CURL_BIN" -fsS -o /dev/null -w '%{http_code}' --max-time 20 "${STAGING_PUBLIC_URL}/" || true)"
  echo "PUBLIC_HEALTH=${health_code}"
  echo "PUBLIC_WEB=${page_code}"
  [[ "$health_code" == "200" && "$page_code" == "200" ]] || staging_die "public staging check failed"
  exit 0
fi

if [[ "$action" == "rollback" ]]; then
  remote rollback "$GIT_SHA" none none
else
  version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$release_dir/release.json")"
  [[ "$version" =~ ^[A-Za-z0-9._-]+$ ]] || staging_die "release version is not safe"
  remote prepare "$GIT_SHA" "$version" none >/dev/null
  staging_scp -r "$release_dir/." "${STAGING_SSH_TARGET}:${STAGING_ROOT}/incoming/${version}/"
  remote activate "$GIT_SHA" "$version" none
fi

if ! public_verification_ok; then
  if [[ "$action" == "deploy" ]]; then
    remote rollback "$GIT_SHA" none none || staging_die "public verification failed and rollback failed"
    staging_die "public verification failed; the previous staging pointer was restored"
  fi
  staging_die "public verification failed"
fi
echo "Staging ${action} verified for ${GIT_SHA}"
