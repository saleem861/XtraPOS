#!/usr/bin/env bash
# Focused checks for the staging activation health retry. This does not install,
# restart, migrate, or contact a service.
set -euo pipefail

helper="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/staging-activate-helper.sh"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/xtrapos-health-retry.XXXXXX")"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/root"
export STAGING_SOURCE_ONLY=yes
export STAGING_TEST_ROOT="$tmp/root"
# shellcheck disable=SC1090
source "$helper"

fail() {
  printf 'FAIL %s\n' "$*" >&2
  exit 1
}

cat >"$tmp/curl" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >>"${CURL_LOG:?}"
count=0
if [ -f "${CURL_COUNT:?}" ]; then
  count="$(tr -d '[:space:]' <"$CURL_COUNT")"
fi
count=$((count + 1))
printf '%s\n' "$count" >"$CURL_COUNT"
case "${CURL_MODE:?}" in
  healthy-third)
    if [ "$count" -lt 3 ]; then
      printf '%s\n%s' '{"status":"Unhealthy"}' '503'
      exit 0
    fi
    printf '%s\n%s' '{"status":"Healthy","version":"0.3.0.0"}' '200'
    ;;
  healthy-spaced)
    printf '%s\n%s' '{ "status" : "Healthy" }' '200'
    ;;
  unhealthy-200)
    printf '%s\n%s' '{"status":"Unhealthy"}' '200'
    ;;
  healthy-503)
    printf '%s\n%s' '{"status":"Healthy"}' '503'
    ;;
  down)
    printf '%s\n%s' '' '000'
    exit 7
    ;;
  *)
    printf 'unexpected curl mode %s\n' "$CURL_MODE" >&2
    exit 1
    ;;
esac
EOF
chmod 755 "$tmp/curl"
export STAGING_CURL_BIN="$tmp/curl"
export CURL_LOG="$tmp/curl-log"
export CURL_COUNT="$tmp/curl-count"

reset_curl() {
  : >"$CURL_LOG"
  printf '0\n' >"$CURL_COUNT"
}

reset_curl
export CURL_MODE=healthy-third
export STAGING_HEALTH_INTERVAL=0
export STAGING_HEALTH_DEADLINE=30
health_ok || fail "a Healthy response after two failures was rejected"
calls="$(tr -d '[:space:]' <"$CURL_COUNT")"
[[ "$calls" == "3" ]] || fail "expected 3 health attempts, got $calls"
grep -q -- 'Host: test-pos.sharbelfashion.com' "$CURL_LOG" || fail "health request omitted the staging Host header"
grep -q -- 'http://127.0.0.1:5088/health' "$CURL_LOG" || fail "health request used the wrong address"
if grep -q -- '5216' "$CURL_LOG"; then
  fail "health request used the production port"
fi

reset_curl
export CURL_MODE=healthy-spaced
health_ok || fail "status Healthy with spaces was rejected"

reset_curl
export CURL_MODE=unhealthy-200
export STAGING_HEALTH_INTERVAL=0
export STAGING_HEALTH_DEADLINE=1
if health_ok >/dev/null 2>&1; then
  fail "HTTP 200 with status Unhealthy was accepted"
fi

reset_curl
export CURL_MODE=healthy-503
if health_ok >/dev/null 2>&1; then
  fail "status Healthy with a non-200 response was accepted"
fi

reset_curl
export CURL_MODE=down
export STAGING_HEALTH_INTERVAL=1
export STAGING_HEALTH_DEADLINE=3
set +e
health_ok >"$tmp/down-out" 2>"$tmp/down-err"
down_code=$?
set -e
[[ "$down_code" -ne 0 ]] || fail "an unreachable health endpoint was accepted"
grep -q 'within 3 seconds' "$tmp/down-err" || fail "deadline failure was not reported"
calls="$(tr -d '[:space:]' <"$CURL_COUNT")"
[[ "$calls" -ge 2 && "$calls" -le 4 ]] || fail "deadline produced $calls attempts"

restored="$tmp/restored"
export CURL_MODE=down
export STAGING_HEALTH_INTERVAL=0
export STAGING_HEALTH_DEADLINE=1
reset_curl
set +e
(
  restore_and_restart() {
    printf 'RESTORED\n' >"$restored"
  }
  wait_until_healthy_or_restore "$tmp/backup" "unchanged"
) >"$tmp/restore-out" 2>"$tmp/restore-err"
restore_code=$?
set -e
[[ "$restore_code" -ne 0 ]] || fail "expired health check returned success"
[[ -f "$restored" ]] || fail "expired health check did not restore the previous release"
grep -q 'within 1 seconds' "$tmp/restore-err" || fail "expired health check did not report the deadline"
grep -q 'previous release was restored' "$tmp/restore-err" || fail "rollback was not reported"

printf 'HEALTH_RETRY_OK\n'
