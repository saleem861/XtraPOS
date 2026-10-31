#!/usr/bin/env bash
# Restore the newest staging pointer backup and restart xtrapos-staging.service only.
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$script_dir/staging-remote-activate.sh" rollback
