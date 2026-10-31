#!/usr/bin/env bash
# Future staging activation plan. Phase 1 always stops before SSH.
# Do not point this script at xtrapos-api.service or port 5216.
set -euo pipefail

public_url="${STAGING_PUBLIC_URL:-https://test-pos.sharbelfashion.com}"
sha="${GIT_SHA:-}"

refuse() {
  echo "::error::$1"
  echo "No SSH connection was opened. No service was restarted. No SQL was run."
  exit 2
}

if [[ "${STAGING_DEPLOY_EXECUTION:-}" != "authorized" ]]; then
  refuse "STAGING_DEPLOY_EXECUTION is not authorized."
fi

if [[ "${STAGING_SSH_CONNECT:-}" != "authorized" ]]; then
  echo "Later verification, not run from this revision:"
  echo "- GET $public_url/ returns HTTP 200."
  echo "- GET $public_url/health returns HTTP 200 and Healthy."
  echo "- GET $public_url/release.json sha equals ${sha:-the built commit}."
  echo "- Assembly version alone is not acceptance."
  echo "- Restart xtrapos-staging.service only. Never restart xtrapos-api.service or bind port 5216."
  refuse "STAGING_SSH_CONNECT is not authorized. Phase 1 does not open SSH."
fi

refuse "SSH execution is disabled in this revision even when the gate variables are set."
