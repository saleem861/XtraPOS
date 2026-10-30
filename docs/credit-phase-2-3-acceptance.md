# Phase 2.3 acceptance checkpoint

Date: 2026-10-27. Repayment, credit UI, and the POS button «تأجيل الدفع» were not started. `CreditSales:Enabled` defaults to false. The button stays `disabled`. No operational database was migrated, and the running API was not restarted.

## Assessment

`PHASE 2.3 ACCEPTED`

Authenticated HTTP coverage and pricing parity with the existing POS pricing engine passed on isolated databases. Operational rollout, the credit button, and repayment remain out of scope. The Git index is still unreadable, so no commit was created.

## Files changed

- `src/XtraPOS.Api/Sales/CheckoutService.cs` — credit quote and persist go through the existing `PrepareLineAsync` / `InsertItemAsync` / stock posting path. Cash checkout still inserts `Draft` and then `Completed`.
- `src/XtraPOS.Api/Credit/CreditSaleDocumentWriter.cs` — delegates pricing and the sale snapshot to `CheckoutService`.
- `src/XtraPOS.Api/Credit/CreditSaleCoordinator.cs` — replay compares deferred amount, customer, and branch. A changed total on an existing operation becomes `ReconciliationRequired`.
- `src/XtraPOS.Api/Credit/CreditReservationService.cs` — finalization conflict includes customer, amount, and branch.
- `src/XtraPOS.Api/Credit/CreditOutboxProcessor.cs` — completion requires the claiming worker's lease.
- `src/XtraPOS.Api/Endpoints/CreditEndpoints.cs` — lines can carry the same override and preview fields as cash checkout. Client user and branch ids are not read.
- `tests/XtraPOS.Api.Tests/CreditSaleIntegrationTests.cs`
- `tests/XtraPOS.Api.Tests/CreditHttpAuthenticationTests.cs`
- `tests/XtraPOS.Api.Tests/XtraPOS.Api.Tests.csproj` — test project references `Microsoft.EntityFrameworkCore.SqlServer` so the SQL fixture can build `PermissionService`.

## Authenticated HTTP

`CreditHttpAuthenticationTests.Authenticated_credit_http_enforces_permission_price_and_scope` passed. It uses cookie login, `X-Device-Id`, and `X-Device-Token` against `WebApplicationFactory`.

| Check | Result |
| --- | --- |
| Anonymous POST | 401 |
| Admin with credit permission, valid lines | 200, cashier is the logged-in user |
| Body `actorUserId` and `branchId` | ignored; sale uses the device context |
| User with no role | 403 |
| Other device header | 401 |
| Foreign cash session and a closed session | 403 |
| Forged total on a new operation | 403, no sale |
| Same operation posted again | 200, one receivable |
| Same operation with a different total | 409 |
| `CreditSales:Enabled=false` | 403 `CREDIT_DISABLED` |
| Limit of 1 against a 5.50 sale | 403 |
| GET of another branch's operation | 404, and it is absent from the list |
| Login cookie | `SameSite=Strict` |

There is no antiforgery token in this API. The CSRF control that exists is the strict auth cookie.

## Pricing parity

Credit calls `CheckoutService.PrepareCreditAsync`, which calls the same `PrepareLineAsync` as cash checkout. Precedence stays: authorized manual override, then quantity-linked special price, then the customer `PriceType`, then `Price0`.

HTTP totals on one item, using stored `ItemPrices` (not a second formula):

| Customer category | Price code | Quantity | Sale total |
| --- | --- | --- | --- |
| Regular | `Price0` | 1 | 5.50 |
| Bronze | `Price3` | 1 | 4.84 |
| Silver | `Price2` | 1 | 4.51 |
| Gold | `Price1` | 1 | 4.29 |
| Dealer | `Dealer` | 1 | 4.00 |
| Special customer | `SpecialCustomer` | 1 | 3.50 |
| Major customer | `MajorCustomer` | 1 | 3.00 |
| Retail | `Retail` | 1 | 5.50 |

Those names are the existing price codes. Bronze, silver, and gold are `Price3`, `Price2`, and `Price1` in `XTRAPOS_UI_STANDARD.md`. They are not separate codes.

Exceptional price: base 5.50, special unit 4.50 from quantity 10, bundle 45. Quantity 9 stayed 49.50. Quantity 10 was 45.00. Quantity 15 was 67.50. An authorized admin unit override of 3.00 was stored as `PriceOverrideMode = UnitPrice`. Recovery does not call pricing again.

Variable-weight lines use the same prepare method and were not given their own HTTP case in this run.

## Money and stock

A sale of 11.00 with 1.00 immediate produced one cash payment of 1.00, one central receivable of 10.00, and one stock movement. The sale is not `Completed` until central finalization. The SQL integration test checks the ledger against open receivables, a single movement after retry, and no second payment.

## Worker recovery

Covered in `CreditSaleIntegrationTests.Credit_sale_outbox_recovers_without_duplicate_financial_rows`, which passed: lost acknowledgement, stale lease versus the owning worker, two workers on one row, expired reservation after commit, corrupted payload, sanitized retry text, and a hosted worker completing a pending sale while `CreditSales:Enabled` is false. Permanent conflicts stay `ReconciliationRequired`. Transient failures stay `Pending` with delay `2^(attempt+1)` seconds, capped at 60. Local sale and outbox completion are one transaction.

## Git index

`git status --short` still fails: `fatal: .git/index: unable to map index file: Invalid argument`.

`attrib .git\index` shows the offline attribute (`O`). A read of the file did not make Git able to map it. The index was not deleted, recreated, or reset. `*-DESKTOP-*` copies were not touched.

Safe next step, before any commit: in Explorer, mark this repo folder "Always keep on this device" and wait until `.git\index` is no longer offline. If that does not clear the error, clone or copy the repository to a folder outside OneDrive and continue there. Do not rebuild the index in place.

## Tests

```text
dotnet test tests\XtraPOS.Api.Tests\XtraPOS.Api.Tests.csproj --filter "FullyQualifiedName~Credit" -p:XtraPosIsolatedOut=%TEMP%\xtrapos-credit-api\ -p:CopyLocalLockFileAssemblies=true --nologo
```

Passed 7, Failed 0, Skipped 0.

| Test | Result |
| --- | --- |
| `CreditAvailabilityTests.Available_subtracts_debt_and_reservations` | Passed |
| `CreditAvailabilityTests.Second_concurrent_request_is_rejected_after_the_first_reservation` | Passed |
| `CreditAvailabilityTests.Covers_limit_edges_and_ignores_expired_holds` | Passed |
| `CreditAvailabilityTests.Defaults_block_credit` | Passed |
| `CreditFoundationTests.Migrations_apply_twice_and_credit_rules_hold_under_concurrency` | Passed |
| `CreditSaleIntegrationTests.Credit_sale_outbox_recovers_without_duplicate_financial_rows` | Passed |
| `CreditHttpAuthenticationTests.Authenticated_credit_http_enforces_permission_price_and_scope` | Passed |

Databases: `XtraPOS_Central_Test_CreditFoundation`, `XtraPOS_Branch_Test_CreditFoundation`, `XtraPOS_Central_Test_CreditSale`, `XtraPOS_Branch_Test_CreditSale`, `XtraPOS_Central_Test_CreditHttp`, `XtraPOS_Branch_Test_CreditHttp`. The tests dropped them.

## Remaining blockers

- Migrations 011 and 024–026 are not on the operational databases.
- The running API does not contain this build.
- The credit button stays disabled, and `CreditSales:Enabled` stays false outside the HTTP test host.
- Variable-weight credit lines were not exercised over HTTP.
- `.git/index` is an offline OneDrive file, so `git status` cannot be reported and no commit was made.
- Repayment and Phase 3 UI were not started.
