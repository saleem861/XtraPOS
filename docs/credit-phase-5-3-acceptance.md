# Phase 5.3.1 acceptance

Assessment: PHASE 5.3.1 ACCEPTED

A completed repayment can be reversed in full on the collecting branch. The original sale, collection, allocation, and repayment ledger row stay in place. Outstanding debt is restored. No reversal UI was added. Operational credit flags stay off.

## 1. Git / source recovery

`.git/index` is still an offline OneDrive placeholder and was not rebuilt. No commit was made. The Phase 5.2 snapshot remains at `C:\XtraPOS-phase52-snapshot`. A recoverable copy of the current `src`, `tests`, and `docs`, including this reversal work and excluding build output, is at `C:\XtraPOS-phase53-work`. That copy is not a Git repository.

## 2. Files

- `src/XtraPOS.Api/Data/Central/Migrations/014_CreditReversals.sql`
- `src/XtraPOS.Api/Data/Migrations/028_CollectionReversals.sql`
- `src/XtraPOS.Api/Credit/CreditReversalService.cs`
- `src/XtraPOS.Api/Endpoints/CreditReversalEndpoints.cs`
- `src/XtraPOS.Api/Credit/CreditOutboxWorker.cs`
- `src/XtraPOS.Api/Sales/CashSessionService.cs`
- `src/XtraPOS.Api/Data/Central/CentralMigrationRunner.cs`
- `src/XtraPOS.Api/Data/Migrations/MigrationRunner.cs`
- `src/XtraPOS.Api/Program.cs`
- `src/XtraPOS.Api/XtraPOS.Api.csproj`
- `tests/XtraPOS.Api.Tests/CreditReversalTests.cs`

## 3. Migrations

Central `014_CreditReversals`: `CreditRepaymentReversals` (one row per original collection) and `CreditAllocationReversals` (one row per original allocation). Branch `028_CollectionReversals`: `BranchCollectionReversals` and `BranchReversalOutbox`.

## 4. Lifecycle

1. Require `Credit.Reverse` or `System.Administer`, an open cash session on the collecting device, and a documented reason.
2. Authorize one central reversal for the completed collection.
3. Insert the branch reversal and outbox row in one local transaction.
4. Mark the central reversal `Posting` and finalize it.
5. The worker claims pending reversal outbox rows and finalizes them. Recovery does not check `CreditReversals:Enabled`.

States used: central `Authorized`, `Posting`, `Completed`, `ReconciliationRequired`; branch `Pending`, `Completed`, `ReconciliationRequired`. A second operation against the same collection is rejected. The same operation id replays. A different reason on the same operation id becomes `ReconciliationRequired`. An authorization that is still `Authorized` after the branch row exists becomes `ReconciliationRequired` and posts no ledger row.

## 5. Cashbox

`RefundToCustomer` stores `RefundOutflow`. `IncorrectRecording` stores `RecordingCorrection`. Both keep the original collection amount and add one reversal amount. Expected drawer cash adds the original collection and subtracts the reversal once. The query uses dynamic SQL so a database that has not received migration 028 still closes a cash session. No sale is created and the original sale total is not changed. If the original session is closed, the reversal is recorded on the device's current open session.

## 6. Permissions

`Credit.Collect` does not authorize reversal. The User and Supervisor roles are rejected. Admin, which has `Credit.Reverse`, is accepted. The audit row records the actor, branch, reason, and the outstanding balance before and after restoration. The audit log stays append-only.

## 7. HTTP

- `GET /api/pos/credit-reversals/enabled`
- `GET /api/pos/credit-reversals/collections/{collectionId}`
- `POST /api/pos/credit-reversals`
- `GET /api/pos/credit-reversals/{operationId}`

`cross-site` requests are rejected. When `CreditReversals:TrustedOrigins` is set, the Origin must match that list, including a same-site Origin that is not on the list and a missing Origin. When the list is empty, the Vite proxy rule remains: a browser `Sec-Fetch-Site` of `same-origin` or `same-site` is accepted. `Sec-Fetch-Site` alone is not treated as proof of a trusted application when trusted origins are configured. The flag defaults to false.

## 8. Idempotency and concurrency

The same reversal operation completes once. A second operation id for the same collection is rejected. Two concurrent reversals produce one reversal ledger row. A conflicting reason replay does not add another allocation reversal.

## 9. Crash recovery

Skipping finalization leaves a pending outbox row. The next recovery completes it. A repeated confirm does not add a second ledger row. A later recovery finds nothing to claim. An authorization left at `Authorized` after the branch commit is marked `ReconciliationRequired`.

## 10. Reconciliation

For a 300 NIS reversal of an 800 NIS sale: outstanding returns from 500 to 800, the repayment ledger row remains, one reversal ledger row points at it, the original allocation remains, one allocation reversal records 300, and the net of that repayment and its reversal is zero. Restoring debt above the credit limit is allowed. New borrowing availability becomes zero.

## 11. Tests

`CreditReversalTests`: 13 passed, 0 failed.

Scenario coverage inside those methods: full 800/300 restore, both cash reasons, idempotent replay, conflicting payload, concurrent attempts, collector and supervisor denial, foreign branch, closed session, worker recovery, lost acknowledgement, reconciliation, over-limit exposure, and authenticated HTTP origin checks.

## 12. Databases

`XtraPOS_Central_Test_Reversal`, `XtraPOS_Branch_Test_ReversalA`, `XtraPOS_Branch_Test_ReversalB`. The fixture drops them at the end. Operational databases were not migrated. The API on port 5080 was not restarted. `CreditSales:Enabled`, `CreditCollections:Enabled`, and `CreditReversals:Enabled` default to false.

## 13. Remaining risks

Partial reversal and a refund from another branch are not implemented. There is still no reversal screen. Physical devices were not part of this phase. Git history is still unreadable. A missing Origin is still accepted when no trusted-origin list is configured, because the auth cookie is `SameSite=Strict`. That is not full CSRF protection.

PHASE 5.3.1 ACCEPTED

# Phase 5.3.2

Assessment: PHASE 5.3.2 ACCEPTED

Both reversal types were completed in the browser on the isolated Branch 02 site and reconciled in SQL. Backend tests are separate evidence and are not the acceptance basis.

1. Source safety. The OneDrive tree already contained Phase 5.3.1. `.git/index` was not rebuilt. No commit was made. `C:\XtraPOS-phase52-snapshot` and `C:\XtraPOS-phase53-work` were left in place.
2. Files. `DebtReversalScreen.tsx`, `DebtRepaymentScreen.tsx`, `PosScreen.tsx`, `CreditReversalService.cs`, `CreditReversalEndpoints.cs`, `CreditReversalTests.cs`.
3. Screen. Full-screen «عكس عملية تسديد دين». The button «عكس تسديد الدين» appears on a completed receipt only when the reversal flag is on and the user has `Credit.Reverse` or `System.Administer`. The amount is read-only. There is no numeric keypad.
4. API reused. `GET /enabled`, `GET /collections/{id}`, `POST /`, `GET /{operationId}`. Notes are required and stored on the audit reason. They are also part of the idempotency payload.
5. Authorization stays on the backend. A collect-only user is still rejected by the existing permission check.
6. Reasons. «إرجاع المبلغ للزبون» is `RefundToCustomer` and requires the cash-returned checkbox. «تصحيح تسجيل تسديد خاطئ» is `IncorrectRecording`.
7. States. Completed, pending, and reconciliation use the backend state and the same operation id from `sessionStorage`.
8. The original receipt stays. A completed reversal shows «تم عكس التسديد» and a separate notice «إشعار عكس تسديد دين».
9. Browser. Vite `http://127.0.0.1:5176` proxied to Branch 02 API `http://127.0.0.1:5097`, database `XtraPOS_Branch_Test_BrowserB`, central `XtraPOS_Central_Test_BrowserE2E`. `CreditReversals:Enabled` was true only on that API. Port 5080 was left running and was not used.
   - RefundToCustomer: cashier opened زبون الدين 800100, receipt `faa7fca1-2741-48e0-91b2-979138f6d5e6`, clicked «عكس تسديد الدين», selected «إرجاع المبلغ للزبون», reason «إرجاع نقدي للزبون في الاختبار», confirmed cash returned, and submitted once. The screen showed «تم عكس التسديد بنجاح» and the notice «إشعار عكس تسديد دين» with resulting debt 800 NIS. Reversal `4fccd051-ad72-4245-bc4e-37ab6cd8287f`.
   - IncorrectRecording: a separate customer 800200, sale 2 of 800 NIS, collection `3d67d4bf-f4d6-46e4-9c75-121e146a968c` of 300 NIS, outstanding 500. The same screen selected «تصحيح تسجيل تسديد خاطئ», reason «النقد لم يُستلم وتسجيل التسديد كان خطأ», left the cash-returned box disabled, and submitted once. Notice showed 800 NIS. Reversal `4a9c3f5c-4fca-48af-a9f6-8a3130e4c237`.
10. SQL. Each customer: original 800, repayment 300 preserved, one reversal 300, outstanding 800. Two payment allocations and two allocation reversals. Branch 02 sales stayed 0. Branch 01 sales are 2 totaling 1600. Cash effects: `RefundOutflow` and `RecordingCorrection`, one each.
11. The reversal page is full-screen RTL. The amount is read-only and no numeric keypad is mounted for it. See the UI correction below for the keyboard measurement.
12. See the UI correction below.
13. Physical iPad and Android were not tested.
14. Databases: `XtraPOS_Central_Test_BrowserE2E`, `XtraPOS_Branch_Test_BrowserA`, `XtraPOS_Branch_Test_BrowserB`.
15. Browser: both reversal submits completed. `CreditReversalTests` remains 13 passed and is not this acceptance. Permission, foreign-branch, pending, and reconciliation states were not repeated in this browser pass.
16. Operational flags stay false. Port 5080 was not restarted.

PHASE 5.3.2 ACCEPTED

## UI correction

The 1100 NIS figure came from adding the reversal amount to an outstanding balance that had already been restored. Posting was not changed. A completed reversal now shows that restored balance. Before completion the screen still shows the server figure, 500 NIS current and 800 NIS expected.

A fresh collection was checked in the browser: 500 NIS before, 800 NIS expected, then «الدين القائم بعد العكس 800 NIS» and a notice of 800 NIS. The text 1100 NIS did not appear. The shared keyboard opened while the reason was focused and measured 356 px of a 1080 px screen, 33 percent. It is not shown on the completed notice. `reversalPreview.test.ts`: 2 passed.

iPad 1180×820 and Android tablet 1280×800 emulation had no horizontal overflow. The confirmation stays in the scrolling form. This browser kept a fine pointer and hover, so the desktop keyboard stayed mounted during that emulation. Physical devices were not tested.

PHASE 5.3.2 UI ACCEPTED
