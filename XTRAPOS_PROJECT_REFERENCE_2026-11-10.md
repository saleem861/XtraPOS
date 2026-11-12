# XtraPOS project reference

Date: 10 November 2026.
This file describes the developed XtraPOS system, its environments, and its accounts.
It does not contain passwords, API secrets, private keys, or connection strings. Those values stay in the secret stores named below. Copying them into this repository would put them in a file that can be committed or uploaded.

## 1. What the product is

XtraPOS is a branch point-of-sale system.

- The browser is a React and TypeScript application in `src/XtraPOS.Web`.
- The server is an ASP.NET Core 8 API in `src/XtraPOS.Api`.
- SQL Server holds one branch database per shop database, collation `Arabic_CI_AS`.
- A separate central database exists only for staging central data and for membership and catalog administration. The local cloud-agent environment must not create `XtraPOS_Central`.
- The first launch is a setup wizard: one business, one branch, the first station, and this device.
- The browser never receives a SQL connection string. Device identity is a device id plus a one-time device token stored in the browser. SQL Server stores only the token hash. The staff session is an HttpOnly cookie named `XtraPOS.Auth`. Staff passwords are stored with the ASP.NET Core password hasher.

A day for business dates on the sales review screen uses `Asia/Hebron`. Many SQL conversions use `Israel Standard Time`. Those are different settings.

The branch-server connection label `متصل بخادم الفرع` is fixed text. A customer must never be shown as a GUID.

## 2. Source layout

| Path | Role |
| --- | --- |
| `src/XtraPOS.Web` | POS screens, Vite, Vitest |
| `src/XtraPOS.Api` | HTTP API, migrations, credit, sales, membership |
| `database` | Notes only. Runtime database files do not live here |
| `deploy` | Staging deploy script and staging notes |
| `docs` | Design notes |
| `.github/workflows/ci.yml` | GitHub CI |
| `.github/workflows/deploy-staging.yml` | Staging inspect, deploy, and rollback. Requires the protected `staging` environment and an explicit approval |

Local commands from the web folder: `npm run dev`, `npm test`, `npm run build`.
API from the repo root: `dotnet run --project src/XtraPOS.Api --launch-profile http`, or `dotnet build XtraPOS.sln -c Debug`.

## 3. Environments

Do not point one environment at another environment’s database.

| Environment | Web | API | Branch database | Central database |
| --- | --- | --- | --- | --- |
| Developer machine | `http://127.0.0.1:5173` | `http://127.0.0.1:5080` | The database named by the local API secrets. Do not retarget it during ordinary UI work | Not used by the local sales screen |
| Cloud agent | `http://127.0.0.1:5173` | `http://127.0.0.1:5080` | `XtraPOS_Branch_Test_Cloud` on `127.0.0.1:1433` only | Must not be created |
| Staging site | `https://test-pos.sharbelfashion.com` | On the server, `http://127.0.0.1:5088`, unit `xtrapos-staging.service` | `XtraPOS_Branch_Test_Staging` | `XtraPOS_Central_Test_Staging` |
| Production | Not used by this work | Production unit `xtrapos-api.service` on port `5216` | Not touched | Not touched |

The Vite dev server proxies `/api` and `/health`. A healthy process returns `{"status":"Healthy"}`. Keep ports 5173 and 5080. Do not start a second API or a second Vite server. Stop the process on 5173 before `npm ci`, because the native bundler file locks on Windows, then start Vite again on the same port.

Staging activation restarts only `xtrapos-staging.service`. It refuses `/opt/xtrapos-api`. The direct staging script is `deploy/Deploy-StagingDirect.ps1`. It builds the working tree, so uncommitted files are included. The release folder name uses `git rev-parse HEAD` even when the tree is dirty. The newest sales-review release recorded on 9 November 2026 is `20261109-220810-2969d0d`.

Staging behavior flags, applied only when the environment is Staging: membership enabled, Google sign-in enabled, Apple sign-in disabled, and special prices do not require verification.

## 4. Accounts and where the secrets live

No password is written here. Use the store named in the last column. If a password is only in a person’s memory or in a local secret file, read it there. Do not paste it back into git.

| Account | Used for | Name | Where the secret is |
| --- | --- | --- | --- |
| Local SQL login | Cloud-agent API | `xtrapos_dev` | `/etc/xtrapos/dev-sql.env`, mode 640. The API receives it through environment variables |
| Developer SQL | Optional local SQL authentication | `xtrapos_dev` when SQL authentication is used | Environment variables `SqlServer__User` and `SqlServer__Password`. If both are unset, the API uses Windows authentication. `appsettings.Development.json` has the server and database name only |
| Staging SQL | Staging branch and staging central | The login configured for the staging API | Server secret file `/etc/xtrapos/staging.env`. The deploy user cannot read this file |
| Staging deploy | Copy a release and activate it | `xtrapos-deploy` at `69.197.178.239` | SSH private key `%USERPROFILE%\.ssh\xtrapos_staging_deploy`. The repo also has `.ssh\xtrapos_staging_deploy` for the script. This key is not a password and must not be copied into a document |
| Staging sudo | Activate a release, or print the staging database name | `xtrapos-deploy` | Passwordless sudo only for `/usr/local/sbin/xtrapos-staging-activate` and `/usr/local/sbin/xtrapos-staging-db-name` |
| GitHub staging | Workflow deploy and rollback | GitHub environment `staging` | GitHub secrets `STAGING_SSH_HOST`, `STAGING_SSH_USER`, `STAGING_SSH_KEY`, `STAGING_KNOWN_HOSTS`. The host value is `69.197.178.239` and the user value is `xtrapos-deploy` |
| First staff administrator | Created by the setup wizard | Chosen at setup, not a fixed built-in password | Stored as a password hash in the branch database |
| Roles | Staff authorization | `Admin`, and `Manager` whose display name was renamed to مشرف | Seeded by branch migrations. Profit and cost on Sales Review also allow `System.Administer` |
| Fictional cloud seed | Optional one-branch demo when setup status is `NotConfigured` | Created by `scripts/cloud-agent-seed-fictional.sh` | Environment variable `XTRAPOS_FICTIONAL_ADMIN_PASSWORD` for that run only. It must not be committed |
| Google membership | Staging customer Google sign-in | Public client id `998725042091-t1p0fv71s7ajkbs104s62if1c1oviqmd.apps.googleusercontent.com` | The client secret is not public. It stays in the staging secret store. The callback is `https://test-pos.sharbelfashion.com/api/membership/auth/google/callback` |
| Apple membership | Prepared and disabled on staging | Apple return URL `https://test-pos.sharbelfashion.com/api/membership/auth/apple/callback` | Apple secret material stays in the staging secret store |

The deploy user cannot read the journal. Health is checked with `GET /health` on the staging API port or through the public site.

## 5. Databases that are allowed and forbidden

Allowed for the work described in the November 2026 sales and collection sessions:

- Staging branch `XtraPOS_Branch_Test_Staging`
- Staging central `XtraPOS_Central_Test_Staging`
- Cloud agent, only if that environment is the one running: `XtraPOS_Branch_Test_Cloud` on localhost

Not targets for this work:

- Production databases and the production service
- `XtraPOS_Central` on a developer or cloud-agent machine
- `XtraPOS_Branch_01` and `XtraPOS_Branch_02` unless the owner explicitly asks to migrate the local database
- `saleemPOSDB`, `Shop1`, `Shop2`, `swstore`

The old Shop1 `dbo.Sales` script was a behavior reference only. It was not a source to import.

At the November staging deploys the branch catalog was about 2814 items and 385 units. Branch migrations had reached `041_OptionalCollectionSession`. Central staging migrations were at 26. Do not invent catalog rows to replace that copy.

## 6. Main behavior that is already developed

### Setup and device

`GET /api/setup/status` returns `NotConfigured`, `Configuring`, `Configured`, or `Error`.
`POST /api/setup/create-branch` creates the branch database when the product’s own setup path is used, applies migrations, and returns a one-time device token. Joining an existing branch is shown and disabled.

### Sales

A completed sale is a `Sales` header plus `SaleItems` lines. The POS cart is separate from posted sales. Closing the print dialog after a posted credit sale keeps the posted sale and clears the cart. The implementation saves one new empty cart and resets the walk-in customer locally. It must not call the walk-in customer change afterwards, because that call used to write the old lines back.

### Sales Review

Opened from the POS button `المبيعات` as a full screen over the sale. Closing it does not change the cart.

The report is one row per sale line. Filters are independent checkboxes. Date defaults to today in `Asia/Hebron`. Time, station, and branch start off. Totals are SQL aggregates over the full filter. The page size is 50. Date plus time is a time-of-day window on each day, and an overnight window uses OR. Item match can be exact or related units. Related units use `ItemUnits`, `InternalUnitItemID`, and `ParentItemID`. Item 1300 is not hard-coded. Employee filtering uses `UserID`. Classification is one list, shown both as a single type dropdown and as a multi-select. Modified prices means `PriceOverrideMode` is not `None`. Export is `sales-review.xlsx`, capped at 5000 lines. Notes display an em dash because the new line has no comments column. Customer name is a live join, not a sale-time snapshot. Another branch is refused in Arabic and is not queried from central sales.

Cost and profit fields are omitted from JSON, Excel, and the order document unless the user is Admin, Manager or Supervisor, or has `System.Administer`. Sales totals remain for `POS.Sell`.

### Credit and collection

Branch credit is recorded in the branch ledger first. Home → Customers → Debts can collect with no open cash shift. That path sends `allowWithoutSession` and a null cash session. POS collection still requires an open shift. Migration 041 makes `BranchDebtCollections.CashSessionID` nullable.

The amount field is focused on entry. An over-limit payment is the Arabic dialog `المبلغ المدفوع يتجاوز الرصيد القابل للتحصيل.` Customer search from the debts menu matches the POS customer search.

A known and unchanged mismatch: the deferral cards can show a central credit policy while the branch decision is `CreditDisabled` because the branch policy row is missing. The message is then the generic refusal `تم رفض البيع بالدين. السلة ما زالت موجودة.`

### Membership

Staging has membership and Google sign-in enabled and Apple disabled. Passkeys and phone verification exist in the schema and services. Their secrets stay in the environment, not in this file.

## 7. Old Windows POS

The behavior reference is the VB.NET application under `D:\XtraposSource\XtraPOS`, especially `SalesFormArabic.vb`, `SalesFormArabic.designer.vb`, and `OrdersFormArabic.vb`.

The old sales grid is a line report. `CanSeeEarns` means admin and manager. The old customer-number filter searched the phone column. The new screen searches `CustomerNumber`. The old comments column has no new-schema equivalent. The old 300-row confirmation was replaced by paging.

## 8. Related status file

The sales-review and collection chronology, including each staging release id from 8 and 9 November 2026, is in `SALES_REVIEW_AND_COLLECTIONS_STATUS_2026-11-10.md`.
