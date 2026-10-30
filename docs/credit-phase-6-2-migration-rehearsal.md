# Phase 6.2 — migration rehearsal

**PHASE 6.2 BLOCKED — the CloudClusters account is not signed in, so the existing central backup cannot be exported.**

No operational database was restored, migrated, or reconfigured. Port 5080 was not restarted. Credit feature flags were not enabled. Nothing was deployed.

The three backups are not one distributed snapshot. Branch files were written at 2026-10-27 22:27:40 local. The central file name records 2026-10-27 22:51:49. Cross-database totals must not be treated as one moment.

## 1. Central backup transfer

Remote file named by the backup record:

`/cloudclusters/mssql/backup/XtraPOS_Central_20261027_225149_E5CF9BB988C2_COPY.bak`

It is not on this PC. `C:\XtraPOS-db-backups` still contains only the two branch COPY_ONLY files. Ports 21, 22, and 990 on `69.30.235.148` are closed from this PC. Port 10173 is open. The stored SQL password was not decrypted.

`https://clients.cloudclusters.io/applications` shows the CloudClusters login form. There is no authorized session for File Manager, Web Shell, or FTPS. The public VPS ordering catalog is not the SQL Server application. No file was staged, no permission was changed, and no public URL was created.

CloudClusters documents single-database download as: open the panel shell, then download by FTPS. Their article also says `chmod 644` on the backup. That was not done. `644` is world-readable on the host, and FTPS is documented as not exposing the protected backup directory until the panel stages the file.

Support request for the account owner:

> Please export the existing SQL Server backup file, without creating a new backup and without changing firewall rules, to the authorized CloudClusters account owner by the panel file export or an account-scoped FTPS drop. Do not publish it on a public URL. File: `/cloudclusters/mssql/backup/XtraPOS_Central_20261027_225149_E5CF9BB988C2_COPY.bak`. Database: `XtraPOS_Central`. Host: `69.30.235.148` port `10173`. Backup type already taken: FULL, COPY_ONLY, CHECKSUM, NO_COMPRESSION, source build 16.0.4015. After export, send the byte size and SHA-256. Place the file only where the FTPS account for this application can read it. Do not use `chmod 644`.

## 2. Checksum and integrity

| Backup | Size | Integrity |
|---|---|---|
| Branch 01 `C:\XtraPOS-db-backups\XtraPOS_Branch_01_20261027-222740_COPY.bak` | 4,565,504 bytes | `RESTORE VERIFYONLY WITH CHECKSUM` valid. Original file not overwritten. |
| Branch 02 `C:\XtraPOS-db-backups\XtraPOS_Branch_02_20261027-222740_COPY.bak` | 3,047,936 bytes | Same. |
| Central remote file | Not transferred | SHA-256, `HEADERONLY`, and `FILELISTONLY` were not run because the file is not local. |

## 3. SQL Server compatibility

Local default instance `DESKTOP-MFTEQK0`: SQL Server 2022, 16.0.1000.6, Developer Edition. Central backup source: SQL Server 2022, 16.0.4015. Both are the same major version. A different cumulative update is not, by itself, a reason to refuse the restore or to install another instance. No second instance was provisioned. The operational instance was not upgraded.

The central file is not local, so `RESTORE HEADERONLY` has not been run. When the file arrives, the scratch restore will be attempted on `DESKTOP-MFTEQK0`. Compatibility will be decided by that restore, not by the build numbers.

Branch backups were taken on 16.0.1000.6 and restored on that same instance.

## 4. Scratch names

Requested names `XtraPOS_Central_RestoreTest_Credit` and `XtraPOS_Branch01_RestoreTest_Credit` are refused by `CentralDatabase.IsAllowedName` and `BranchDatabase.IsAllowedName`. The runners accept `XtraPOS_Central` or `XtraPOS_Central_Test_*`, and `XtraPOS_Branch`, `XtraPOS_Branch_##`, or `XtraPOS_Branch_Test_*`.

Databases created:

- `XtraPOS_Branch_Test_Restore01` on `DESKTOP-MFTEQK0`
- `XtraPOS_Branch_Test_Restore02` on `DESKTOP-MFTEQK0`

`XtraPOS_Central_Test_RestoreCredit` was not created. An empty central database would not be the backup.

Files:

- `C:\XtraPOS-db-backups\scratch-data\XtraPOS_Branch_Test_Restore01.mdf` and `.ldf`
- `C:\XtraPOS-db-backups\scratch-data\XtraPOS_Branch_Test_Restore02.mdf` and `.ldf`

`WITH REPLACE` was not used. Original operational paths were not reused. Logical names from `RESTORE FILELISTONLY`: `XtraPOS_Branch_01` / `XtraPOS_Branch_01_log` and `XtraPOS_Branch_02` / `XtraPOS_Branch_02_log`.

## 5. Restore results

Both restores finished with checksum and recovery. Branch 01 scratch: 2,953 pages. Branch 02 scratch: 1,673 pages. Both databases are ONLINE. `XtraPOS_Branch_01` and `XtraPOS_Branch_02` remained ONLINE and were not the restore targets.

## 6. DBCC CHECKDB

`DBCC CHECKDB` with `NO_INFOMSGS, ALL_ERRORMSGS` returned no messages for either scratch database.

## 7. Pre-migration baselines

`XtraPOS_Branch_Test_Restore01`, before 024–028:

| Measure | Value |
|---|---|
| Customers | 4 |
| Sales | 5 |
| Sales total | 851.0000 |
| Payments | 3 |
| Payment total | 780.0000 |
| Cash sessions | 1 |
| Stock movements | 2,128 |
| Items | 2,814 |
| Last migration | `023_QuantitySpecialPrices` |

`XtraPOS_Branch_Test_Restore02` had 28 tables and stopped at `011_ItemEditPermissions`. It had no `Customers` or `Sales` tables. That is the backup contents, not a failed restore.

## 8. Migration order

Branch runner, `MigrationRunner` through `--apply-branch-migrations`, only when `--confirm-database` equals the configured database and the name is allowed:

`001` through `023`, then `024_CreditPermissions`, `025_CreditSaleOutbox`, `026_CreditOutboxLease`, `027_DebtCollections`, `028_CollectionReversals`.

Central runner order, not executed: `001`–`010`, then `011_CustomerCredit`, `012_CreditCollections`, `013_ReceivableSaleNumber`, `014_CreditReversals`. `--apply-central-migrations` confirms the saved central name `XtraPOS_Central` and was not run.

## 9. Migration results

Executed with `SqlServer__Server=localhost` and the scratch database name, content root `C:\XtraPOS-phase61-repo\src\XtraPOS.Api`.

Restore01 applied 024–028. Counts after: items 2,814, movements 2,128, users 145, permissions 24 (was 17), migrations 28.

Restore02 applied 012–028 because the backup stopped at 011. Those scripts create empty structures. They did not import debts.

Credit tables on Restore01 after migration: `BranchFinancialOutbox` 0, `BranchDebtCollections` 0, `BranchCollectionReversals` 0, permissions 24.

Operational history after this work: `XtraPOS_Branch_01` still ends at `023_QuantitySpecialPrices`. `XtraPOS_Branch_02` still ends at `014_StockMovementSource`. The Branch 02 backup is older than that live history (backup ends at 011). This rehearsal did not apply 012–014 to the operational database. Those three migrations were already on the live Branch 02 before this command; the backup does not contain them.

## 10. Idempotency

The same command was run a second time on each scratch database. Every migration, including 024–028, reported already applied. Permission count stayed 24. No second copy of credit tables was created.

## 11. Before and after

Restore01 customers 4, sales 5 totaling 851.0000, payments 3 totaling 780.0000, stock movements 2,128. Unchanged. No receivables or ledger rows were inserted by the migration. Restore02 had no sales to preserve; the new tables are empty.

## 12–15. Smoke tests, variable weight, HTTP security, reconciliation

Not run. They require the restored central database and isolated APIs pointed only at that scratch central. They were not pointed at `XtraPOS_Central`, `XtraPOS_Branch_01`, `XtraPOS_Branch_02`, or port 5080.

## 16. Files modified

This document. No migration script was changed. No operational configuration was changed.

## 17. Tests

No new automated test run in this rehearsal. The blocked item is backup access and restore compatibility, not a failing test.

## 18. Remaining blockers

1. Sign in at `https://clients.cloudclusters.io/applications`. Open the SQL Server deployment for `69.30.235.148`. On Shell/SSH, copy `/cloudclusters/mssql/backup/XtraPOS_Central_20261027_225149_E5CF9BB988C2_COPY.bak` into the FTPS directory owned by that deployment's FTPS user. Do not `chmod 644` and do not email a public link. Start FTPS from the panel FTPS page and download with explicit FTP over TLS into `C:\XtraPOS-db-backups`.
2. On `DESKTOP-MFTEQK0`, run `RESTORE HEADERONLY` and `RESTORE FILELISTONLY`, then restore to a new `XtraPOS_Central_Test_*` database with `WITH MOVE` and no `WITH REPLACE`. Decide compatibility from that result.
3. Then create `XtraPOS_Central_Test_RestoreCredit` with `WITH MOVE`, run `DBCC CHECKDB`, apply central migrations 011–014 twice, and run smoke tests A–F against the three scratch databases only.

The Branch 02 backup is also behind the live Branch 02 schema (011 versus 014). A newer COPY_ONLY of Branch 02 is required before a production rehearsal of that branch. The Branch 01 backup matches the live migration head `023`.

## 19. Production runbook (documentation only)

Do not execute this against live databases until the blockers above are closed and a new rehearsal reports accepted.

1. Take a new FULL `COPY_ONLY`, `CHECKSUM` backup of `XtraPOS_Central` on `69.30.235.148,10173` and of each branch. Record each backup's own time. They will not be one snapshot.
2. `RESTORE VERIFYONLY WITH CHECKSUM` on a build that can read the central backup (16.0.4015 or newer).
3. Restore each backup to a new database whose name the runners allow (`XtraPOS_Central_Test_*`, `XtraPOS_Branch_Test_*`). `WITH MOVE` to new files. Never `WITH REPLACE`. Never the operational file paths.
4. `DBCC CHECKDB` each scratch database.
5. Record customer, sale, payment, cash-session, and stock totals at decimal(19,4).
6. Branch order on each scratch branch: existing 001–023, then 024, 025, 026, 027, 028, via `--apply-branch-migrations --confirm-database` set to that scratch name. Run twice.
7. Central order on the scratch central only: 011, 012, 013, 014, via a connection whose database name is the scratch central. Do not confirm `XtraPOS_Central` for a rehearsal.
8. Compare totals. Stop if any pre-existing sale, payment, or stock total changes.
9. Deploy branch and central API builds that contain the same migrations. Keep `CreditSales:Enabled`, `CreditCollections:Enabled`, and `CreditReversals:Enabled` false.
10. Turn flags on only for a named pilot branch after the scratch smoke tests pass: credit sales, then collections, then reversals. Recovery of an already stored operation stays allowed when the flag is off.
11. Watch `BranchFinancialOutbox` and `BranchReversalOutbox` for pending and `ReconciliationRequired`.
12. If a migration fails before any credit posting exists, restore the pre-migration COPY_ONLY to a new name, fix the script, and rehearse again. Do not restore an older backup over a database that already has credit postings. After real credit sales exist, roll forward: keep the immutable sale, collection, and reversal rows, repair with a new migration, and reconcile outstanding from the ledger.

**PHASE 6.2 BLOCKED — sign in at `https://clients.cloudclusters.io/applications`, open the SQL Server deployment, and copy the existing backup into that deployment's FTPS directory. The applications page is the login form. Direct SSH, FTP, and FTPS ports are closed.**
