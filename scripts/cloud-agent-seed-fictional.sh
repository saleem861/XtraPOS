#!/usr/bin/env bash
# Optional fictional branch for local cloud development.
# Not used by startup. Requires the API on port 5080 and an unconfigured
# XtraPOS_Branch_Test_Cloud. The admin password is read from the environment
# and is never stored in this script.
set -euo pipefail

secrets="/etc/xtrapos/dev-sql.env"
if [[ ! -f "${secrets}" ]]; then
  echo "Local SQL environment file is missing." >&2
  exit 1
fi

set -a
# shellcheck disable=SC1090
source "${secrets}"
set +a

if [[ "${SqlServer__Database:-}" != "XtraPOS_Branch_Test_Cloud" ]]; then
  echo "Refusing to seed a database other than XtraPOS_Branch_Test_Cloud." >&2
  exit 1
fi
if [[ "${SqlServer__Server:-}" != "localhost" && "${SqlServer__Server:-}" != "127.0.0.1" ]]; then
  echo "Refusing to seed a SQL Server host other than localhost." >&2
  exit 1
fi
if [[ -z "${XTRAPOS_FICTIONAL_ADMIN_PASSWORD:-}" ]]; then
  echo "Set XTRAPOS_FICTIONAL_ADMIN_PASSWORD before running this optional seed." >&2
  exit 1
fi

status="$(curl -fsS http://127.0.0.1:5080/api/setup/status)"
if [[ "${status}" != *'"status":"NotConfigured"'* ]]; then
  echo "The isolated test database is already configured. Leaving it unchanged."
  exit 0
fi

curl -fsS http://127.0.0.1:5080/api/setup/create-branch \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/x-ndjson' \
  --data "$(python3 - <<'PY'
import json, os
print(json.dumps({
    "businessName": "متجر السحابة التجريبي",
    "branchName": "فرع التجربة",
    "branchCode": "99",
    "branchType": "Retail",
    "stationNumber": 1,
    "stationName": "كاونتر 1",
    "deviceName": "صندوق التجربة",
    "username": "cloud.admin",
    "displayName": "مدير التجربة",
    "password": os.environ["XTRAPOS_FICTIONAL_ADMIN_PASSWORD"],
    "confirmPassword": os.environ["XTRAPOS_FICTIONAL_ADMIN_PASSWORD"],
    "operatingMode": "Standalone",
    "linkToCentral": "no",
    "branchLocation": "Local",
    "branchRemoteChoice": "no",
    "centralDatabaseName": None,
}, ensure_ascii=False))
PY
)"
echo
