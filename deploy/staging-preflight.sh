#!/usr/bin/env bash
# Validate a staging request. This script does not open SSH.
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

if [[ "${ACTION:-}" == "deploy" || "${ACTION:-}" == "rollback" ]]; then
  note PASS "action is ${ACTION}"
else
  note BLOCKED "action must be deploy or rollback"
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
  note BLOCKED "git_sha was well formed but this run has no repository"
fi

if [[ "${STAGING_ENVIRONMENT_PROTECTION:-}" == "verified" ]]; then
  note PASS "STAGING_ENVIRONMENT_PROTECTION=verified"
else
  note BLOCKED "STAGING_ENVIRONMENT_PROTECTION must be verified after the staging Environment has a required reviewer and allows main only"
fi

echo
echo "Deploy and rollback stay skipped until that variable is set."
echo "They also require secrets STAGING_SSH_KEY and STAGING_KNOWN_HOSTS."
echo "The host is administrator@69.197.178.239, the service is xtrapos-staging.service, the API port is 5088, and the root is /opt/xtrapos-staging."
echo "No SSH, deployment, service restart, or SQL migration was performed."

if [[ "$blocked" -ne 0 ]]; then
  echo "::error::Staging deployment is blocked."
  exit 1
fi
