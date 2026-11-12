# XtraPOS handover — 2 Nov 2026, Passkey frontend staging

Continue from this file. The diagnostic Passkey frontend is built locally and is not on staging.

## Do not do

- Do not deploy until the user explicitly authorizes a frontend-only publish.
- Do not run `deploy/Deploy-StagingDirect.ps1`. It publishes the API and calls `xtrapos-staging-activate`, which runs branch and central migrations and restarts the staging API.
- Do not replace `/opt/xtrapos-staging/current`. That symlink selects both `web` and `api`.
- Do not edit Nginx, install or change `/usr/local/sbin/xtrapos-staging-activate`, restart `xtrapos-api` or `xtrapos-staging`, or run migrations.
- Do not use `-WriteCentral`. Do not print SQL passwords, SSH private keys, or `central-connection.json`.
- Do not delete `artifacts/web-ci`. `src/XtraPOS.Web/node_modules` is a junction to `artifacts/web-ci/node_modules`.
- Do not start customer-reference-data repairs in this thread.
- Do not mark a physical Android Passkey test as PASS. Status remains PENDING.

## Git

- Repository: `D:\Google\XtraPOS`
- HEAD: `aaba7df3f412dbf59c699ffdbff32ee345f861e2` on `main`
- The working tree is dirty. The SHA is not the same as the built frontend. Do not commit unless asked.

## Local frontend

- Diagnostic code: `src/XtraPOS.Web/src/passkey/phonePasskey.ts`, `src/XtraPOS.Web/src/passkey/phonePasskey.test.ts`, `src/XtraPOS.Web/src/components/PasskeyPhonePage.tsx`
- Tests: 12 passed in `src/passkey/phonePasskey.test.ts`
- Production bundle: `src/XtraPOS.Web/dist/assets/index-CAc0yqwN.js`
- SHA-256: `E71BD4738704C1F3F48C3E00431CEBCC6F0DC81045C110AFBD58311BBAD31FEA`
- The bundle contains `PASSKEY_BROWSER_NOT_ALLOWED`, `PASSKEY_PLATFORM_UNAVAILABLE`, `PASSKEY_INSECURE_CONTEXT`, `credentials.create`, `credentials.get`, and `submission`.
- `NotAllowedError` is `PASSKEY_BROWSER_NOT_ALLOWED`. It is not treated as user cancellation.
- Platform authenticator only. No security-key fallback. No challenge, token, or key logging.
- Rebuild with `npm run build` in `src/XtraPOS.Web` if `dist` is missing. Do not run `npm ci` unless the junction is broken.

## Staging, unchanged

- Site: `https://test-pos.sharbelfashion.com`
- SSH: `xtrapos-deploy@69.197.178.239`, key `%USERPROFILE%\.ssh\xtrapos_staging_deploy`, BatchMode, IdentitiesOnly. Git Bash is required because Windows `ssh.exe` has crashed with empty output. Quote the key path; the Windows profile path contains a space.
- Active release: `20261102-124008-aaba7df`
- Nginx root: `/opt/xtrapos-staging/current/web`
- `current` -> `/opt/xtrapos-staging/releases/20261102-124008-aaba7df`
- Live script: `/assets/index-uHBueDcT.js`
- Staging API: `127.0.0.1:5088`, unit `xtrapos-staging.service`, working directory `current/api`
- Production API: `/opt/xtrapos-api`, unit `xtrapos-api.service`, port 5216. The deploy user cannot write `/opt/xtrapos-api`.
- `xtrapos-deploy` can write `/opt/xtrapos-staging` and `current/web`.

## Frontend-only deploy script

`deploy/Deploy-StagingFrontendOnly.ps1` is prepared and was not used to upload.

- Default `-Mode DryRun` passed on 2 Nov 2026: hash matched, `PROBE_OK`, `DRY_RUN_OK`.
- `-Mode Deploy` and `-Mode Rollback` are implemented and were not executed.
- Deploy copies `dist` to `/opt/xtrapos-staging/frontend-releases/<id>/`, checks hashes, then adds new hashed assets under `current/web/assets`.
- It atomically replaces `index.html`, `favicon.svg`, `sw.js`, `manifest.webmanifest`, and `frontend-release.json`.
- It does not change `release.json`, the `current` symlink, the API, Nginx, or production.
- The first deploy snapshots the live web root as `frontend-00000000-000000-baseline` before switching. Rollback restores root files from `frontend-releases` and does not delete hashed assets.
- The apply script passed `bash -n`. It has not run on the server. `/opt/xtrapos-staging/frontend-releases` did not exist at the dry run.

Authorized command, only after the user says to publish:

```powershell
powershell.exe -NoProfile -File D:\Google\XtraPOS\deploy\Deploy-StagingFrontendOnly.ps1 -Mode Deploy
```

Then check `https://test-pos.sharbelfashion.com/` serves `index-CAc0yqwN.js`, `/health` is Healthy, and the staging API release is still `20261102-124008-aaba7df`.

## Passkey behavior to preserve

- New-customer save is enabled by Passkey or OTP. Passkey success must not mark the phone verified.
- Save copy: new customer «تم حفظ الزبون بنجاح»; edit «تم حفظ تعديلات الزبون بنجاح». Verification success is «تم التحقق من الزبون بنجاح»; failure is «فشل التحقق من الزبون».
- Failed POS verification keeps the customer name and number and uses ordinary pricing for the current sale only.
- Public QR origin is `https://test-pos.sharbelfashion.com`. RP ID is the hostname only.
- Passkey rows are stored on the branch connection, not the central database.

## Left unfinished outside this frontend

- Customer type and price type lists are empty because the staging central database had zero businesses when reference migration 016 ran. Do not invent a business or copy production data.
- The installed activation helper is the older one-shot health check. A local retry helper exists in `deploy/staging-activate-helper.sh` and was not installed.
- Physical Android enrollment and authentication remain PENDING.
