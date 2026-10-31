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

if [[ "${ACTION:-}" == "inspect" || "${ACTION:-}" == "deploy" || "${ACTION:-}" == "rollback" ]]; then
  note PASS "action is ${ACTION}"
else
  note BLOCKED "action must be inspect, deploy, or rollback"
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

echo
echo "The staging Environment supplies approval and the SSH secrets."
echo "Allowed target: xtrapos-deploy@69.197.178.239, service xtrapos-staging.service, port 5088, root /opt/xtrapos-staging."
echo "No SSH, deployment, service restart, or SQL migration was performed by preflight."

if [[ "$blocked" -ne 0 ]]; then
  echo "::error::Staging deployment is blocked."
  exit 1
fi
