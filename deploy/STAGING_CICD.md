# Staging CI/CD — confirmation still required

Phase 1 adds CI and a deployment workflow that cannot reach the server. Nothing in this file authorizes a deployment.

`https://test-pos.sharbelfashion.com` was healthy at assembly version `0.3.0.0` on 31 Oct 2026, 17:37 Asia/Hebron. nginx 1.24.0 serves the site. The static `index.html` file date was 29 Oct 2026, 09:20 Asia/Hebron. That assembly version is not a git commit. The first accepted deployment must match the full commit SHA in `release.json`.

## Workflows

| File | What it does | What it refuses |
|---|---|---|
| `.github/workflows/ci.yml` | On pull requests and pushes to `main`: Node.js 22, full Vitest, web production build, .NET 8 solution build, API publish, secret scan, release artifact | SQL, SSH, service restarts, GitHub Environments |
| `.github/workflows/deploy-staging.yml` | Manual preflight for a full SHA already on `main` | SSH, file copy, restart, SQL. It does not declare `environment: staging`, so dispatch cannot create an unprotected environment |
| `.github/workflows/publish-staging.yml` | Retired. A manual run with `staging` fails immediately | Build, artifact upload, and the old `environment: staging` job |

The API integration suite stays out of CI. Those tests expect SQL Server on localhost with integrated security, and `MainBranchSetupExecutionTests` still expects 15 `DatabaseSchemaHistory` rows while the branch schema has 32 migrations.

## Artifact

`xtrapos-release-<sha>` contains `api/`, `web/`, and `release.json`. The same manifest is copied to `web/release.json` so nginx can serve `/release.json` after a future copy. Fields are `version`, `sha`, `createdAt`, and `migrations: not-applied`. `deploy/scan-release.sh` rejects `appsettings`, `.env`, key files, certificates, private keys, and credential assignments.

## Verification a later deployment must perform

Health and the assembly version are not enough.

1. `GET https://test-pos.sharbelfashion.com/` returns HTTP 200.
2. `GET https://test-pos.sharbelfashion.com/health` returns HTTP 200 and a body containing `Healthy`.
3. `GET https://test-pos.sharbelfashion.com/release.json` returns the same full git SHA that the workflow built.
4. If the check fails, restore the previous staging pointer and restart `xtrapos-staging.service` only.

`deploy/staging-remote-activate.sh` prints this plan and exits before SSH. `deploy/staging-preflight.sh` is the gate the workflow runs.

## Items that still need confirmation

Do not invent these values. Do not store SQL passwords in GitHub.

### SSH username and trusted host fingerprint

- The scripts mention `69.197.178.239` only as an example. A login as `xtrapos` at that address was denied. The username is unknown.
- The trusted host key fingerprint is not recorded in the repository.
- After approval, the names to create are secrets `STAGING_SSH_HOST`, `STAGING_SSH_USER`, `STAGING_SSH_KEY`, and `STAGING_KNOWN_HOSTS`.
- The account must be able to write only under `/opt/xtrapos-staging` and must not be able to restart `xtrapos-api.service`.

### nginx upstream and staging API port

- The public host is nginx 1.24.0. `/` is a static file. `/health` is proxied and returns JSON.
- The upstream listen port is not in the repository. Do not change the nginx site, certificates, or DNS to discover it.
- Production `xtrapos-api.service` on port 5216 is out of scope. Port 5216 is an operator constraint; it is not named in the existing deploy scripts. Those scripts only mention port 5080, which is the local development API.

### `current.txt` versus `current/` symlink

- `deploy/Publish-Staging.ps1 -Remote` copies to `/opt/xtrapos-staging/releases/<version>` and writes `/opt/xtrapos-staging/current.txt`.
- The setup log path recorded in the handover is `/opt/xtrapos-staging/current/api/App_Data/logs/setup.log`.
- These two layouts are not the same. The server has not been inspected in this phase. Do not switch either pointer until the live layout is confirmed.
- `deploy/Rollback-Staging.ps1` swaps `current.txt` and `previous.txt` on a local `STAGING_ROOT`. It does not use SSH and does not restart a service. The remote publish path does not write `previous.txt`, so the first remote copy can lose the rollback pointer.

### Persistent configuration and Data Protection keys

- `appsettings*.json` and `.env` files are gitignored and must stay on the server.
- `deploy/staging.env.example` names `XtraPOS_Branch_Test_Staging` and `ASPNETCORE_ENVIRONMENT=Staging`. The live server file was not read. Do not overwrite it from the example.
- Data-protection keys are created under the machine common-application directory `XtraPOS/data-protection-keys`. `App_Data` (central connection, device database list, setup log, sync status) lives in the API content root.
- A new release directory must reuse those existing host directories. Copying a fresh publish over them would make existing protected values unreadable.
- `deploy/sql/provisioning-review.sql` contains `USE XtraPOS_Central`. No workflow may execute it. Do not modify `XtraPOS_Central`.

### Backup and SSH rollback

Not implemented, and not possible until the pointer layout is confirmed.

- Before a future switch, copy the active pointer and the previous pointer to `/opt/xtrapos-staging/backups/<sha>-<timestamp>/`.
- Do not take a SQL backup of `XtraPOS_Central` or any operational branch database as part of this pipeline.
- Rollback restores that pointer and restarts `xtrapos-staging.service` only, then repeats the SHA and health checks.
- There is no SSH rollback script that is allowed to run in this phase.

## Repository variables

Leave these unset until the matching confirmation is written down. Setting them does not enable SSH in this revision.

| Variable | Value that means confirmed | What must be true first |
|---|---|---|
| `STAGING_ENVIRONMENT_PROTECTION` | `verified` | GitHub Environment `staging` exists, requires a reviewer, and allows deployment from `main` only |
| `STAGING_SSH_CONFIG` | `verified` | Username, host key fingerprint, and a staging-only key are stored as secrets |
| `STAGING_SERVER_CHECKS` | `verified` | Upstream port, pointer layout, and persistent config and key paths are recorded |
| `STAGING_DEPLOY_EXECUTION` | `authorized` | A separate approval to deploy, after the three confirmations above |

`STAGING_SSH_CONNECT` is intentionally not a repository variable. This revision refuses SSH even if someone exports it in a shell.
