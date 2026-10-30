# XtraPOS — Project Handover

**Date:** 29 October 2026  
**Workspace:** `D:\Google\XtraPOS`  
**Purpose:** Resume development on a new Cursor account from the last verified state.  
**Do not treat this file as a replacement for the approved specification.**

Approved functional specification (source of truth for setup, devices, sync, and reset):

`D:\Google\XtraPOS\XtraPOS_Setup_Device_Central_Handover_2026-10-29.md`

UI rules: `D:\Google\XtraPOS\XTRAPOS_UI_STANDARD.md`

This document separates **verified** facts from **unfinished** work. It contains no passwords, tokens, or private keys.

---

## 1. Architecture

### Frontend

- React 19 + Vite 8 + TypeScript.
- Project: `D:\Google\XtraPOS\src\XtraPOS.Web`
- Dev server port **5173**. Vite proxies `/api` to `http://localhost:5080` (`XTRAPOS_API` can override that). Preview port is 4173.
- Entry: `src\XtraPOS.Web\src\App.tsx`. Setup choice, wizard, login, and home are phases in that file.
- Device identity in the browser: IndexedDB database `xtrapos-device`, store `config` (`src\XtraPOS.Web\src\storage\deviceConfig.ts`). A local Windows agent is optional and is tried first, with a 400 ms timeout.

### Backend

- ASP.NET Core, .NET 8, version 0.3.0.
- Project: `D:\Google\XtraPOS\src\XtraPOS.Api`
- Solution: `D:\Google\XtraPOS\XtraPOS.sln`
- HTTP endpoints are mapped from `src\XtraPOS.Api\Endpoints\ApiEndpoints.cs` and related endpoint files. Composition root: `src\XtraPOS.Api\Program.cs`.
- Local launch profile: `http://0.0.0.0:5080`, `ASPNETCORE_ENVIRONMENT=Development` (`src\XtraPOS.Api\Properties\launchSettings.json`).

### SQL Server

- Branch operational connection is configured with user secrets (server + database name only in the secrets file that was inspected). Development database name: **`XtraPOS_Branch_01`** on **localhost**.
- Central connection is a separate encrypted file, not user secrets. See section 6.
- Branch schema migrations: `src\XtraPOS.Api\Data\Migrations`, runner `MigrationRunner`.
- Central schema migrations: `src\XtraPOS.Api\Data\Central\Migrations`, runner `CentralMigrationRunner`.
- SQL files are embedded resources in `XtraPOS.Api.csproj`. A new `.sql` file must be added there and to the runner list or it will not run.

### Directory map

| Path | Role |
|---|---|
| `src\XtraPOS.Web` | React app |
| `src\XtraPOS.Api` | API |
| `src\XtraPOS.Api\Setup` | First-time setup, provisioning, setup log |
| `src\XtraPOS.Api\Devices` | Device registration, POS numbers, reset policy, sync status |
| `src\XtraPOS.Api\Sync` | Central connection file, shared-data sync |
| `src\XtraPOS.Api\Data\Migrations` | Branch migrations `001`–`032` |
| `src\XtraPOS.Api\Data\Central\Migrations` | Central migrations `001`–`015` |
| `tests\XtraPOS.Api.Tests` | API tests |
| `tools\XtraPOS.DeviceAgent` | Optional Windows local agent |
| `deploy` | Staging package/check/rollback scripts |
| `artifacts\local-run` | Published local API currently listening on 5080 |
| `artifacts\api-build`, `artifacts\api-test` | Isolated build outputs used so the locked exe could be rebuilt |

---

## 2. Current development status

### Implemented and verified

- Welcome screen with three choices only: **فرع مستقل**, **نقطة تابعة لفرع**, **إدارة مركزية**. No long authorization field on that screen. Verified in the browser at `http://localhost:5173` on 29 Oct 2026.
- **فرع مستقل** opens `SetupWizard` with the device role locked to `MainBranch`. The first wizard step is **هل يتبع إدارة مركزية؟** (**نعم** / **لا**). Verified by clicking through.
- **لا** continues to **كيف تريد إعداد قاعدة بيانات هذا الفرع؟** (new or existing), then location. Choosing an existing local database called the API and listed `XtraPOS_Branch` and `XtraPOS_Branch_01`. No database was selected or written.
- **نعم** continues to **إعداد الإدارة المركزية** (create or use an existing central database). Verified only through that step.
- Branch migration `032_POSDeviceConfigurations` applied on `XtraPOS_Branch_01`. It creates `dbo.POSDeviceConfigurations` and `dbo.CentralPosReplica` if missing. Before/after counts on that apply: Items 2814, Users 145 (unchanged). The same command also applied previously pending `031_CustomerPasskeys`.
- `GET /api/sync/status` returned HTTP 200 after the local API restart.
- `GET /api/setup/existing-configuration` returned the organization and branch already stored in `XtraPOS_Branch_01`.
- `DeviceSetupPolicyTests`: 3 passed (POS number suggestion, protected-database drop refusal, JSON merge).
- `tools\XtraPOS.DeviceAgent` compiles. It is not installed and was not listening on port 47321.

### Partially implemented

- Independent-branch wizard. Navigation for the central yes/no question and the new/existing database question works. The full approved paths are not finished: users-first sync as a blocking step, Admin password verification, writing recovered settings into `device-config.json`, and completing setup without touching live customer databases.
- Child point (**نقطة تابعة لفرع**) still opens `UnregisteredDeviceScreen` (`App.tsx`), not the full wizard secondary path.
- **إدارة مركزية** from the welcome screen also still opens `UnregisteredDeviceScreen` with `intent: "central"`, which is the older create form. It does not open `SetupWizard` with `CentralOnly`.
- POS number allocation (`PosNumberAllocator`, `CentralPosRegistryService`, `GET /api/pos/numbers/suggest`, `POST /api/pos/numbers/reserve`) is coded. Suggest failed at runtime because the central connection cannot be decrypted. Central migration `015` was **not** applied.
- Local POS replica (`CentralPosReplica`, `CentralPosReplicaSync`, `PosReplicaHostedService`, `POST /api/sync/pos-replica`) is coded. It cannot read the central registry until the central connection and migration `015` exist.
- Header sync badge (`SyncStatusBadge`) is on every screen. At verification time it showed **الاتصال غير متاح**.
- Device configuration writer runs after new-branch and secondary-device registration (`DeviceConfigurationWriter`). It was not exercised against a live setup.
- Admin reset UI (**إعادة تهيئة النقطة**) posts deregister and delete flags. The UI always sends `exclusiveLocal: false`, so it cannot drop a database. `ProtectedDatabasePolicy` refuses `XtraPOS_Central`, `المقاطعة`, system databases, shared databases, missing backups, and unsynced financial activity.
- Branch SQL passwords can be stored with data protection (`BranchSqlSecretStore`, `POST /api/branches/sql-access`). No password was saved. The central endpoint table from migration `015` is not in the database.
- Setup progress labels were reordered so “مزامنة المستخدمين من المركزية” appears before “التحقق من حساب المدير”. The server in `SetupService` still creates the station and device, then runs central user bootstrap later in `ApplyTopologyAsync`. That is not yet the handover’s blocking users-first sequence.

### Not implemented

- End-to-end users-first synchronization with a real progress bar, then verification of any authorized central Admin, without creating a new Admin, on a central-linked branch.
- Automatic fill of the review form from an existing branch into `device-config.json` / `POSDeviceConfigurations` as specified in handover section 5. `GET /api/setup/existing-configuration` only returns organization and branch identity.
- Continuous, tested copy of the central POS registry to every branch and point.
- Direct central-to-branch SQL connectivity test with secrets kept only on the server.
- Installed XtraPOS Device Agent and proof that Chrome, Edge, and Firefox on the same PC share one device file.
- Chrome on Android and Chrome on iOS device tests.
- Safe exclusive local database deletion. The policy exists; no `DROP DATABASE` is reachable from the UI.
- Staging publish of this work. `https://test-pos.sharbelfashion.com` was not updated.

---

## 3. Most recent work (29 Oct 2026)

These files were added or changed for the approved setup architecture. They are in the working tree. Do not assume they are committed.

**Branch migration**

- `src\XtraPOS.Api\Data\Migrations\032_POSDeviceConfigurations.sql`
- Registered in `MigrationRunner` and `XtraPOS.Api.csproj`

**Central migration (file only, not applied)**

- `src\XtraPOS.Api\Data\Central\Migrations\015_CentralPosAndBranchSql.sql`
- Creates `dbo.CentralPosRegistry` and `dbo.BranchSqlEndpoints` if missing. No passwords. Registered in `CentralMigrationRunner` and the csproj.

**API**

- `Devices\PosNumberAllocator.cs`
- `Devices\CentralPosRegistryService.cs`
- `Devices\CentralPosReplicaSync.cs`
- `Devices\PosReplicaHostedService.cs`
- `Devices\ProtectedDatabasePolicy.cs`
- `Devices\DeviceConfigDocument.cs`
- `Devices\DeviceConfigurationWriter.cs`
- `Devices\SyncStatusStore.cs` writes `App_Data\sync-status.json`
- `Devices\BranchSqlSecretStore.cs`
- `Endpoints\ApiEndpoints.cs` — reset body, `/api/sync/status`, `/api/sync/pos-replica`, `/api/pos/numbers/suggest`, `/api/pos/numbers/reserve`, `/api/branches/sql-access`, `/api/setup/existing-configuration`
- `Program.cs` — those services, the replica hosted service, and a persistent data-protection key directory
- `Setup\SetupService.cs` and `Devices\SecondaryDeviceRegistration.cs` call `DeviceConfigurationWriter` after device insert

**Web**

- `components\SyncStatusBadge.tsx` and `styles\app.css`
- `App.tsx` mounts the badge. Independent branch sets `deviceRole: "MainBranch"`, `roleLocked: true`, and phase `wizard`.
- `components\HomeScreen.tsx` reset dialog
- `storage\deviceConfig.ts` agent read with a 400 ms abort
- `types\setup.ts` progress-step label order

**Agent and tests**

- `tools\XtraPOS.DeviceAgent\Program.cs` and `.csproj` — `http://127.0.0.1:47321/device-config`, file `C:\ProgramData\XtraPOS\device-config.json`
- `tests\XtraPOS.Api.Tests\DeviceSetupPolicyTests.cs`

**Data protection change**

`Program.cs` copies any `key-*.xml` from the user ASP.NET key folder into:

`C:\ProgramData\XtraPOS\data-protection-keys`

and persists new keys there. The application name was **kept** as the historical path:

`C:\Users\Saleem Jayousi\OneDrive\Projects\XtraPOS\src\XtraPOS.Api`

That path does not exist on this PC. The name was kept so a recovered original key would still match. Changing it would make the old ciphertext permanently unreadable even if the key file is found later.

---

## 4. Current runtime (verified 29 Oct 2026, about 13:14)

| Process | What | Port |
|---|---|---|
| `node` PID 18000 | Vite dev server, cwd `src\XtraPOS.Web` | **5173** listening |
| `XtraPOS.Api` PID 22748 | `D:\Google\XtraPOS\artifacts\local-run\XtraPOS.Api.exe` | **5080** listening |
| Device Agent | not running | **47321** not listening |

The API was started with:

- `ASPNETCORE_ENVIRONMENT=Development`
- `ASPNETCORE_URLS=http://0.0.0.0:5080`
- `ASPNETCORE_CONTENTROOT=D:\Google\XtraPOS\src\XtraPOS.Api\bin\Debug\net8.0`

Content root matters. `App_Data\central-connection.json` and setup logs live under that content root, not under `artifacts\local-run`.

`bin\Debug\net8.0\XtraPOS.Api.exe` may still be an older build. The process that is actually serving is `artifacts\local-run\XtraPOS.Api.exe`. Rebuilding into `bin\Debug` or `artifacts\local-run` fails while PID 22748 has those files locked. Use another output directory, or stop only this local process. Do not stop a Windows service named `xtrapos-api`.

User secrets id: `8f3c1a2e-6b44-4d91-9c70-1a2b3c4d5e6f`. Inspected keys are `SqlServer:Server=localhost` and `SqlServer:Database=XtraPOS_Branch_01`. No SQL password is in that secrets file. Integrated security is implied when user and password are both empty (`SqlConnectionFactory`).

---

## 5. Database state

### Verified

- Local API database: **`XtraPOS_Branch_01`** on localhost.
- `GET /api/setup/status` returned `Configured` for that database (business and branch names were returned; do not copy customer data into new docs).
- Branch migrations applied through **`032_POSDeviceConfigurations`**. History count after the command was 32. `031` and `032` were applied in that run. Item and user counts did not change.
- Existing-database discovery also saw **`XtraPOS_Branch`** (branch name “الفرع الأول”). It was not modified.
- Saved central file `src\XtraPOS.Api\bin\Debug\net8.0\App_Data\central-connection.json` points at database **`XtraPOS_Central`**. Authentication is SQL. The password field is data-protection ciphertext. Server, port, and username are in that file; do not copy them into chat or new docs.

### Not applied

- Central migration **`015_CentralPosAndBranchSql`**. It was reviewed. It only creates two tables if they are absent. It was not run because the saved target is `XtraPOS_Central` and there was no approval to change that database. No backup was taken.

### Safety restrictions (do not relax)

- Never `DROP` or rewrite **`XtraPOS_Central`**, **المقاطعة**, `XtraPOS_Branch_01`, or `XtraPOS_Branch` as part of device reset.
- `ProtectedDatabasePolicy` treats `XtraPOS_Central`, system databases, and any name containing `المقاطعة` as protected.
- Branch migration command: `--apply-branch-migrations --confirm-database <exact configured name>`. The confirmed name must equal the configured name.
- Central migration command: `--apply-central-migrations --confirm-database <exact saved central database name>`. It refuses to run until the central connection file decrypts.
- Staging packager `deploy\Publish-Staging.ps1` refuses operational names `XtraPOS_Branch_01`, `XtraPOS_Branch_02`, and `XtraPOS_Central`.
- Production service **`xtrapos-api`** must not be restarted or modified. Staging service, when used later, is **`xtrapos-staging`** only.
- Do not print connection strings, data-protection keys, or `protectedPassword`.

Other database names mentioned in the approved handover and not re-checked in this session: `XtraPOS_Branch_Test_Staging`, `XtraPOS_Branch_Test_I29`, `XtraPOS_Central_Test_Staging`. Treat their existence as **not re-verified**.

---

## 6. Bugs and blockers

1. **Central data-protection key is missing.** Decrypting `central-connection.json` throws: key `{98e0f577-2afa-46ff-83f5-1cbe06e33b25}` was not found. The only key on this Windows user is `{ddfc36aa-3735-4129-b8f3-0cc21df82afd}`, now also copied to `C:\ProgramData\XtraPOS\data-protection-keys`. The Saleem OneDrive project path does not exist. No plaintext central password is stored in appsettings or user secrets, so the file was not rewritten. Until the original key file or the SQL password is available, POS suggest/reserve, replica pull, and central migration `015` cannot run.

2. **Independent-branch wizard is only partly done.** The first questions work. These approved steps are not completed end to end:
   - Users and roles sync **before** Admin verification, with real progress, retry, and a diagnostic id.
   - Admin screen accepts any active central Admin for the organization. Do not create a local Admin on the linked path.
   - Existing database restores organization, branch, external SQL (non-secret), stations, counters, and cashboxes into a review form and then into `device-config.json` and `POSDeviceConfigurations`.
   - Standalone path still creates the first local Admin. That path must not copy another organization’s users.

3. **Welcome choices for point and central still use the old screen** `UnregisteredDeviceScreen`, not the wizard paths in `wizardFlow.ts`.

4. **Sync badge is not a live central checkpoint.** It shows the file/memory snapshot from `SyncStatusStore`. Offline is expected while the central connection cannot be read.

5. **Device Agent is not installed.** Browsers do not share `device-config.json`. `HttpListener` on `127.0.0.1:47321` also needs a Windows URL reservation before it can start. Do not claim cross-browser sharing.

6. **Staging SSH is not configured.** `STAGING_SSH` is unset. `known_hosts` contains `69.197.178.239` and `api.sharbelfashion.com`. The local public key comment is `xtrapos-sharbel-accountant`. A BatchMode attempt as `xtrapos@69.197.178.239` was denied. Deploy scripts do not record the username. Do not guess further usernames.

7. **`GET /api/pos/numbers/suggest` failed** for the reason in item 1. Not a logic failure of `PosNumberAllocator`.

---

## 7. Approved decisions

Follow `XtraPOS_Setup_Device_Central_Handover_2026-10-29.md` section 0 and the flow in section 1. Do not redesign.

In particular:

- One welcome screen, three choices, no long authorization field.
- Independent branch asks whether it follows central management before any other setup question.
- Central-linked branch syncs users first, then verifies an existing Admin. No new Admin.
- Existing database is a restore, not a second installation.
- Central reaches branch SQL only from the backend. No SQL passwords in the browser or `device-config.json`.
- Central POS numbers are allocated from the central registry, smallest positive free number, reserved in a transaction. Revoked numbers stay reserved.
- Device settings live in the agent/browser file, `POSDeviceConfigurations`, and the central registry. Do not mix those layers.
- Reset clears this point only. Central deregistration and exclusive local database deletion are optional and off by default.
- Connection loss must not wipe device configuration.

Section 17 of that handover is a historical snapshot. This file replaces it for “what is done now”.

---

## 8. Important files

**Specification and UI**

- `D:\Google\XtraPOS\XtraPOS_Setup_Device_Central_Handover_2026-10-29.md`
- `D:\Google\XtraPOS\XTRAPOS_UI_STANDARD.md`
- `D:\Google\XtraPOS\PROJECT_HANDOVER.md` (this file)

**Web**

- `src\XtraPOS.Web\src\App.tsx`
- `src\XtraPOS.Web\src\components\SetupChoiceScreen.tsx`
- `src\XtraPOS.Web\src\components\SetupWizard.tsx`
- `src\XtraPOS.Web\src\setup\wizardFlow.ts`
- `src\XtraPOS.Web\src\components\UnregisteredDeviceScreen.tsx`
- `src\XtraPOS.Web\src\components\HomeScreen.tsx`
- `src\XtraPOS.Web\src\components\SyncStatusBadge.tsx`
- `src\XtraPOS.Web\src\storage\deviceConfig.ts`
- `src\XtraPOS.Web\src\types\setup.ts`
- `src\XtraPOS.Web\vite.config.ts`

**API**

- `src\XtraPOS.Api\Program.cs`
- `src\XtraPOS.Api\Endpoints\ApiEndpoints.cs`
- `src\XtraPOS.Api\Setup\SetupService.cs`
- `src\XtraPOS.Api\Setup\SetupLog.cs` — `App_Data\logs\setup.log`, ids `SETUP-…`
- `src\XtraPOS.Api\Sync\CentralConnectionFileStore.cs`
- `src\XtraPOS.Api\Sync\SqlEndpointSettings.cs`
- `src\XtraPOS.Api\Data\SqlConnectionFactory.cs`
- `src\XtraPOS.Api\Data\BranchMigrationCommand.cs`
- `src\XtraPOS.Api\Devices\` (files listed in section 3)

**Deploy**

- `deploy\Publish-Staging.ps1`
- `deploy\Check-Staging.ps1`
- `deploy\Rollback-Staging.ps1`
- `deploy\staging.env.example`

**Tests**

- `tests\XtraPOS.Api.Tests\DeviceSetupPolicyTests.cs`
- Existing wizard tests under `src\XtraPOS.Web\src\components\SetupWizard.*.test.tsx` were not re-run after the latest navigation check.

---

## 9. Deployment

**Local (current work):** Web `http://localhost:5173`, API `http://127.0.0.1:5080`, database `XtraPOS_Branch_01`. Stay here until explicitly told to publish.

**Staging (not updated in this work):** `https://test-pos.sharbelfashion.com`. Package with `deploy\Publish-Staging.ps1`. Remote copy needs `STAGING_SSH` and then a restart of **`xtrapos-staging` only**. The script does not apply migrations and does not touch port 5080. Check with `Check-Staging.ps1` when `STAGING_WEB_URL` is set. Staging example database names are `XtraPOS_Branch_Test_Staging` and `XtraPOS_Central_Test_Staging` (`staging.env.example`). Those were not confirmed live.

**Production:** service `xtrapos-api`. Do not restart, publish over, or migrate it. `XtraPOS_Central` is the saved central database and must not receive migration `015` without an explicit approval and a backup.

---

## 10. Tests

| Check | Result |
|---|---|
| `dotnet test --filter DeviceSetupPolicyTests` with `-p:UseAppHost=false -p:XtraPosIsolatedOut=D:\Google\XtraPOS\artifacts\api-test\` | **Passed, 3/3** |
| API publish to `artifacts\local-run` | **Succeeded** after stopping the previous local process |
| `GET http://127.0.0.1:5080/api/sync/status` | **200**, state Success at the time of the call |
| `GET /api/setup/status` and `/api/setup/existing-configuration` | **200** against `XtraPOS_Branch_01` |
| `GET /api/pos/numbers/suggest` | **Failed**, central key missing |
| Browser: welcome → independent → central question → no → new/existing → existing DB list | **Passed** on 29 Oct 2026. No save. |
| Browser: yes → central create/existing question | **Passed** only to that step |
| Users-first sync, Admin password, device-config restore, replica sync, reset, agent, mobile Chrome | **Not verified** |
| Wizard Vitest suite and full API suite | **Not re-run** |
| HTTPS Staging | **Not deployed, not verified** |

A full test run that copies into `src\XtraPOS.Api\bin\Debug\net8.0\XtraPOS.Api.exe` fails while a process locks that file. Use `XtraPosIsolatedOut` or `UseAppHost=false`.

---

## 11. NEXT TASK

Development stopped after the wizard’s first screens were verified in the browser and the central connection remained undecryptable. No Staging publish. No central migration.

Immediate next implementation, in order:

1. **Do not hunt the repository again.** Start from `App.tsx`, `SetupWizard.tsx`, `wizardFlow.ts`, and `SetupService.ApplyTopologyAsync`.
2. Keep **فرع مستقل** on the verified first step (**هل يتبع إدارة مركزية؟**). Do not send that choice back to `UnregisteredDeviceScreen`.
3. Finish the **لا** path on a **new test database name only** (`XtraPOS_Branch_Test_…`). New or existing database, local Admin, station, device row in `POSDeviceConfigurations`, login. Do not run it against `XtraPOS_Branch_01`.
4. Finish the **نعم** path only after the central key or password is restored: sync users first (real progress), then Admin username/password for an existing central Admin, then branch register and POS number reserve. If the key is still missing, stop that path and leave the UI on a clear diagnostic. Do not invent a central password and do not apply migration `015` to `XtraPOS_Central`.
5. Point **نقطة تابعة** and **إدارة مركزية** at the existing wizard roles (`Secondary`, `CentralOnly`) instead of the old create form, reusing `SecondaryDeviceRegistration` and the central setup services.
6. Re-test in the browser at `http://localhost:5173`. Do not publish.

Blocked until a human supplies one of: the missing data-protection key file `key-98e0f577-2afa-46ff-83f5-1cbe06e33b25.xml`, or the central SQL password so `central-connection.json` can be re-protected with the current key ring. Also blocked: Staging SSH username.

---

## 12. Development rules for the next session

- Local only: Web **5173**, API **5080**, until a later instruction says to publish.
- Preserve `XtraPOS_Central`, `XtraPOS_Branch_01`, `XtraPOS_Branch`, and المقاطعة. No destructive SQL. No production service changes.
- Reuse `SetupService`, `MigrationRunner`, `CentralConnectionFileStore`, `SetupWizard`, and `wizardFlow.ts`. Do not rewrite them.
- Do not add a second POS table. Central allocation belongs in `CentralPosRegistry` after `015` is approved. Branch copy belongs in `CentralPosReplica`.
- Do not scan the whole repository. Open the files in section 8.
- Do not put secrets in source, logs, or this handover.
- The local API process is PID **22748** at the time of writing. If it has exited, start `artifacts\local-run\XtraPOS.Api.exe` with the environment in section 4. If the source has changed since that publish, publish to a new folder first or the running exe will not match the tree.
- Vite was started with `npm run dev` in `src\XtraPOS.Web`. If port 5173 is down, start it again. Do not restart it just to read code.

---

## Assumptions (not verified)

- Staging still serves an older build. This session did not open `https://test-pos.sharbelfashion.com`.
- `XtraPOS_Branch_Test_Staging` and `XtraPOS_Branch_Test_I29` still exist. They were not queried.
- The wizard steps after “existing database list” and after “central database choice” still match `visibleSteps` in `wizardFlow.ts`. They were not clicked, because the next actions can write.
- Git commit state was not checked. Treat the files in section 3 as working-tree changes until `git status` says otherwise.
