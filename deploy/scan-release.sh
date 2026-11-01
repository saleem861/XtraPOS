#!/usr/bin/env bash
# Fail when a release directory contains host secrets or encryption material.
# The check looks at file names and a few assignment patterns. It does not print matching lines.
set -euo pipefail

root="${1:-}"
if [[ -z "$root" || ! -d "$root" ]]; then
  echo "::error::Usage: deploy/scan-release.sh <release-directory>"
  exit 1
fi

forbidden=0
while IFS= read -r -d '' path; do
  echo "::error::Forbidden release file: ${path#"$root"/}"
  forbidden=1
done < <(find "$root" -type f \( \
  -name 'appsettings*.json' -o \
  -name '.env' -o -name '.env.*' -o \
  -name 'secrets.json' -o \
  -name 'dev-sql.env' -o \
  -name 'staging.env' -o -name 'staging.env.local' -o \
  -name '*.pfx' -o -name '*.p12' -o -name '*.pem' -o \
  -name 'key-*.xml' -o \
  -name 'id_rsa' -o -name 'id_ed25519' -o -name '*.ppk' \
  \) -print0)

if grep -R -l -E -e 'SqlServer__Password=[^[:space:]]+' -e 'MSSQL_SA_PASSWORD=[^[:space:]]+' -e 'STAGING_SSH_KEY=[^[:space:]]+' "$root" >/dev/null; then
  echo "::error::Forbidden credential assignment found in the release directory. The matching line was not printed."
  forbidden=1
fi

if [[ "$forbidden" -ne 0 ]]; then
  echo "::error::Release scan failed. The artifact must not contain appsettings, environment files, keys, or credential assignments."
  exit 1
fi

if [[ ! -f "$root/release.json" || ! -f "$root/web/release.json" ]]; then
  echo "::error::release.json is missing from the release root or the web directory."
  exit 1
fi

python3 - "$root/release.json" "$root/web/release.json" <<'PY'
import json, pathlib, sys
root, web = (json.loads(pathlib.Path(path).read_text()) for path in sys.argv[1:])
if root != web:
    raise SystemExit("release.json at the release root and under web differ.")
sha = root.get("sha", "")
if len(sha) != 40 or any(ch not in "0123456789abcdef" for ch in sha):
    raise SystemExit("release.json sha must be the full git commit SHA.")
if root.get("migrations") != "not-applied":
    raise SystemExit("release.json must record migrations as not-applied.")
for key in ("password", "connectionString", "secret", "appsettings"):
    if key.lower() in {name.lower() for name in root}:
        raise SystemExit("release.json contains a forbidden field.")
PY

echo "Release scan passed for $root"
