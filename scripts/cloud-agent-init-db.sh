#!/usr/bin/env bash
# Create the isolated cloud test database if it is missing, then apply pending
# branch migrations. Does not create a business, branch, or admin account.
# Refuses every database other than XtraPOS_Branch_Test_Cloud on localhost.
set -euo pipefail

export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/local/bin:${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

root="$(pwd)"
if [[ ! -f "${root}/XtraPOS.sln" ]]; then
  echo "Run this script from the XtraPOS repository root." >&2
  exit 1
fi

secrets="/etc/xtrapos/dev-sql.env"
if [[ ! -f "${secrets}" ]]; then
  echo "Local SQL environment file is missing. Refusing to initialize a database." >&2
  exit 1
fi

set -a
# shellcheck disable=SC1090
source "${secrets}"
set +a

database="${SqlServer__Database:-}"
server="${SqlServer__Server:-}"
if [[ "${database}" != "XtraPOS_Branch_Test_Cloud" ]]; then
  echo "Refusing to initialize a database other than XtraPOS_Branch_Test_Cloud." >&2
  exit 1
fi
if [[ "${server}" != "localhost" && "${server}" != "127.0.0.1" ]]; then
  echo "Refusing to initialize SQL Server on a host other than localhost." >&2
  exit 1
fi
if [[ -z "${SqlServer__User:-}" || -z "${SqlServer__Password:-}" ]]; then
  echo "SQL login is incomplete. Refusing to initialize." >&2
  exit 1
fi

if [[ ! "${SqlServer__User}" =~ ^[A-Za-z][A-Za-z0-9_]{0,127}$ ]]; then
  echo "Refusing a SQL login name that is not a plain identifier." >&2
  exit 1
fi

sql_query() {
  local login="$1"
  local password="$2"
  local query="$3"
  SQLCMDPASSWORD="${password}" sqlcmd -S localhost -U "${login}" -C -b -h-1 -W -Q "${query}"
}

if [[ -n "${MSSQL_SA_PASSWORD:-}" ]]; then
  sql_query sa "${MSSQL_SA_PASSWORD}" "
SET NOCOUNT ON;
IF DB_ID(N'XtraPOS_Branch_Test_Cloud') IS NULL
    CREATE DATABASE [XtraPOS_Branch_Test_Cloud] COLLATE Arabic_CI_AS;
DECLARE @collation nvarchar(128) = (SELECT collation_name FROM sys.databases WHERE name = N'XtraPOS_Branch_Test_Cloud');
IF @collation <> N'Arabic_CI_AS'
    THROW 50001, N'Refusing to change the collation of XtraPOS_Branch_Test_Cloud.', 1;
USE [XtraPOS_Branch_Test_Cloud];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'${SqlServer__User}')
    CREATE USER [${SqlServer__User}] FOR LOGIN [${SqlServer__User}];
IF IS_ROLEMEMBER(N'db_owner', N'${SqlServer__User}') <> 1
    ALTER ROLE db_owner ADD MEMBER [${SqlServer__User}];
"
else
  present="$(sql_query "${SqlServer__User}" "${SqlServer__Password}" "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'XtraPOS_Branch_Test_Cloud') IS NULL THEN 0 ELSE 1 END;")"
  if [[ "${present}" != "1" ]]; then
    echo "The isolated test database is missing and no local administrator login is available to create it." >&2
    exit 1
  fi
fi

cd "${root}/src/XtraPOS.Api"
dotnet run --launch-profile http -o /tmp/xtrapos-cloud-init -- --apply-branch-migrations --confirm-database XtraPOS_Branch_Test_Cloud
