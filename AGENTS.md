# XtraPOS Cloud Agent notes

These notes are for Cursor Cloud Agents previewing this repository. The product behavior stays in `README.md`, `XTRAPOS_UI_STANDARD.md`, and the handover documents.

## Preview

- Web: `http://127.0.0.1:5173` (Vite). Open this port in Cursor Forwarded Ports.
- API: `http://127.0.0.1:5080`, launch profile `http`.
- The dev server proxies `/api` and `/health` to the API. Check `GET /health` on either port. A healthy process returns `{"status":"Healthy"}`.
- Keep these ports. Do not start a second API or a second Vite server.

Cloud Agents boot with Node.js 22 (`/usr/local/bin/node`) and the .NET 8 SDK (`/usr/share/dotnet`). `scripts/cloud-agent-install.sh` restores dependencies. `scripts/cloud-agent-start.sh` starts SQL Server, the API, and Vite. Both are safe to run again.

## Isolated database

The only database this environment may use is the local SQL Server Developer instance listening on `127.0.0.1:1433`.

- Database: `XtraPOS_Branch_Test_Cloud`
- Collation: `Arabic_CI_AS`
- Login: `xtrapos_dev`, supplied to the API through environment variables
- Credential file: `/etc/xtrapos/dev-sql.env` (mode 640). Do not print it, commit it, or copy it into logs.

`XtraPOS_Central` does not exist here and must not be created, migrated, or queried. Do not point the API at `XtraPOS_Branch_01`, `XtraPOS_Branch_02`, `saleemPOSDB`, `Shop1`, `Shop2`, `swstore`, or any host other than localhost. Do not deploy to `test-pos.sharbelfashion.com` and do not change production VPS services.

The test database is empty. Startup lists pending branch migrations and does not apply them. Do not run `--apply-branch-migrations` or `--apply-central-migrations` unless the user explicitly approves that command and the confirmed database name is exactly `XtraPOS_Branch_Test_Cloud`. Do not complete the setup wizard in a way that creates a business or a central database without that approval.

## Resource-saving mode

Follow `XTRAPOS_UI_STANDARD.md` section 27. Inspect only the files the task needs, keep the current ports, reuse the running API and web processes, and run focused tests instead of the full suite. A successful build is not evidence that a screen works end to end.

## Commands

From the repository root:

```bash
bash scripts/cloud-agent-install.sh
bash scripts/cloud-agent-start.sh
```

Web build and tests, from `src/XtraPOS.Web`: `npm run build`, `npm test`.

API build, from the repository root: `dotnet build XtraPOS.sln -c Debug`.
