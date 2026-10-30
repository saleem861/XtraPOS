# Phase 5.1.1 acceptance

Assessment: PHASE 5.1.1 ACCEPTED

Authenticated collection HTTP and cross-branch SQL reconciliation were exercised on disposable databases. Operational flags stay false. The repayment screen and reversal were not started.

## 1. Git

`.git/HEAD` is readable and points at `refs/heads/master`. `.git/index` remains an offline OneDrive placeholder (`A O P`). `git status` fails with `unable to map index file: Invalid argument`. The index was not rebuilt, reset, or copied. No commit was created. Manual step: Explorer “Always keep on this device” on `.git/index`, or move a verified tree outside OneDrive. Do not recreate the index.

## 2. Files changed in 5.1.1

- `src/XtraPOS.Api/Endpoints/CreditCollectionEndpoints.cs` — qualified status columns; confirm rejects a foreign `Origin`.
- `tests/XtraPOS.Api.Tests/CreditCollectionHttpTests.cs` — cookie host tests.
- `docs/credit-phase-5-1-acceptance.md`

Phase 5.1 service, migrations 012 and 027, and the branch collection outbox are unchanged in role.

## 3. HTTP routes

- `GET /api/pos/credit-collections/enabled`
- `GET /api/pos/credit-collections/customers/{id}/summary`
- `GET /api/pos/credit-collections/customers/{id}/receivables`
- `POST /api/pos/credit-collections/quote`
- `POST /api/pos/credit-collections`
- `GET /api/pos/credit-collections/{operationId}`
- `GET /api/pos/cash-session/current` for drawer cash

## 4. Authentication

Anonymous POST returns 401. A user with `Credit.Collect` succeeds when the test host sets `CreditCollections:Enabled=true`. A view-only user receives 403 on confirm. A user with no credit permission receives 403 on the debt summary. A body `actorUserId` does not change the cashier stored from the cookie. Login `Set-Cookie` is `SameSite=Strict` and HttpOnly. There is no antiforgery token. Confirm also returns 403 when `Origin` is another site. That is the CSRF control that was verified; it is not a general antiforgery framework.

## 5. Branch and device isolation

A foreign device token returns 401. A cash session id from the other branch returns 403. A closed session returns 403. Branch A cannot read Branch B’s collection status (404). Branch B can read its own.

## 6. Cross-branch amounts

Branch 01 owns the 800 NIS receivable. Branch 02 collects 300 NIS over HTTP. Central outstanding becomes 500 NIS. Branch 02 has one collection row for that operation, attributed to its cashier, device, and open session. Branch 02 sales count stays 0. Branch 01 sale total stays 800 NIS. The ledger repayment and the allocation for that operation are each 300 NIS. Branch 01’s summary then reads 500 NIS outstanding.

## 7. Allocation and overpayment

FIFO of 300 NIS settles the 200 NIS older invoice and 100 NIS of the 350 NIS newer invoice. A later 50 NIS payment targets the selected newer invoice. A stale 250 NIS request against the remaining 200 NIS is rejected. Two branches posting 400 NIS against 500 NIS produce one success and one refusal, leaving 100 NIS. Client `outstanding` is ignored.

## 8. Idempotency

The same operation id returns 200 again and does not add a second cash row, allocation, or ledger entry. The same id with 301 NIS instead of 300 NIS returns 409.

## 9. Cashbox

Collections are `BranchDebtCollections`, not sales. Expected drawer cash equals the sum of pending, completed, and reconciliation-required cash collections on the open session. A released authorization has no cash row. Reconciliation-required cash remains after an expired authorization.

## 10. Recovery

With `CreditCollections:Enabled=false`, the host still finalizes a durable pending collection exactly once. A second worker does not add a ledger row. A live lease blocks another worker. An expired authorization behind committed cash becomes `ReconciliationRequired` and is not released. A new POST while the central database name is missing returns 503 and does not collect.

## 11. Databases

`XtraPOS_Central_Test_CollectHttp`, `XtraPOS_Branch_Test_CollectHttpA`, `XtraPOS_Branch_Test_CollectHttpB`. The earlier SQL test uses `XtraPOS_Central_Test_CreditCollect`, `XtraPOS_Branch_Test_CreditCollectA`, and `XtraPOS_Branch_Test_CreditCollectB`. All are dropped by the tests.

## 12. Counts

HTTP test methods: 8 passed, 0 failed, 0 skipped. SQL integration method `CreditCollectionTests`: 1 passed. These are separate methods. No unit-only test was counted as cross-branch proof.

## 13. Blockers

Git index is unusable. Migrations 011, 012, and 024–027 are not applied to operational databases. The running API was not restarted. Operational `CreditSales:Enabled` and `CreditCollections:Enabled` stay false. Reversal is not implemented. The repayment UI was not started.

PHASE 5.1.1 ACCEPTED
