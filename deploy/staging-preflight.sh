#!/usr/bin/env bash
# Print the staging deployment block list and exit.
# This script does not open SSH, restart a service, or run SQL.
set -euo pipefail

blocked=0

note() {
  local state="$1"
  local item="$2"
  echo "$state $item"
  if [[ "$state" == "BLOCKED" ]]; then
    blocked=1
  fi
}

if [[ "${CONFIRM:-}" != "staging" ]]; then
  note BLOCKED "confirm must be the word staging"
else
  note PASS "confirm is staging"
fi

sha="${GIT_SHA:-}"
if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]]; then
  note BLOCKED "git_sha must be the full 40-character commit SHA"
elif git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git fetch --no-tags origin main
  if git merge-base --is-ancestor "$sha" origin/main; then
    note PASS "git_sha $sha is on main"
  else
    note BLOCKED "git_sha $sha is not on main"
  fi
else
  note BLOCKED "git_sha was well formed but this run has no repository to prove it is on main"
fi

check_var() {
  local name="$1"
  local expected="$2"
  local value="${3:-}"
  if [[ "$value" == "$expected" ]]; then
    note PASS "$name=$expected"
  else
    note BLOCKED "$name must be $expected before a later revision can deploy. Current value: ${value:-empty}"
  fi
}

check_var STAGING_ENVIRONMENT_PROTECTION verified "${STAGING_ENVIRONMENT_PROTECTION:-}"
check_var STAGING_SSH_CONFIG verified "${STAGING_SSH_CONFIG:-}"
check_var STAGING_SERVER_CHECKS verified "${STAGING_SERVER_CHECKS:-}"
check_var STAGING_DEPLOY_EXECUTION authorized "${STAGING_DEPLOY_EXECUTION:-}"

echo
echo "Still required before the first staging deployment:"
echo "- GitHub Environment staging with required reviewers and deployment branch main."
echo "- SSH username, private key, and trusted host fingerprint stored as secrets after approval."
echo "- nginx upstream port, current.txt versus current/ symlink, and persistent config and key paths."
echo "- A backup and an SSH rollback path for xtrapos-staging.service only."
echo "- A follow-up change that is allowed to connect. This revision never connects."
echo
echo "No SSH, deployment, service restart, SQL migration, or production change was performed."

if [[ "$blocked" -ne 0 ]]; then
  echo "::error::Staging deployment is blocked. See the BLOCKED lines above."
  exit 1
fi

echo "Gate variables are present. The deploy job still refuses to open SSH in this revision."
