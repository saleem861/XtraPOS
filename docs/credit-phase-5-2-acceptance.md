# Phase 5.2.1 acceptance

Assessment: PHASE 5.2 ACCEPTED

The browser scenario ran on isolated Branch 01 (`127.0.0.1:5174` → API `5095`) and Branch 02 (`127.0.0.1:5175` → API `5096`). Port 5080 was not used. SQL on the three test databases matches the 800 / 300 / 500 result.

## 1. Git / source snapshot

`.git/index` is still an offline OneDrive placeholder. It was not rebuilt. No commit was made. A file snapshot of `src`, `tests`, and `docs` was copied to `C:\XtraPOS-phase52-snapshot`. That copy is not a Git repository and does not repair history.

## 2. Files changed

- `src/XtraPOS.Api/Data/Central/Migrations/013_ReceivableSaleNumber.sql`
- `src/XtraPOS.Api/Credit/CreditReservationService.cs`
- `src/XtraPOS.Api/Credit/CreditSaleCoordinator.cs`
- `src/XtraPOS.Api/Endpoints/CreditCollectionEndpoints.cs`
- `src/XtraPOS.Web/src/credit/debtAccount.ts`
- `src/XtraPOS.Web/src/components/DebtAccountScreen.tsx`
- `src/XtraPOS.Web/src/components/DebtRepaymentScreen.tsx`
- `tests/XtraPOS.Api.Tests/CreditSaleIntegrationTests.cs`

## 3. Invoice number

`Sales.SaleNumber` on the origin branch is the invoice number. Finalization copies it once into `CustomerReceivables.SourceSaleNumber`. It is not edited later. The debt account and receipt show `فاتورة {number}` only when that value is present. Otherwise they show `مرجع الذمة` plus the receivable id. A missing origin branch does not invent a number. `SourceSaleID` stays the link.

The isolated credit-sale test compared `Sales.SaleNumber` with `CustomerReceivables.SourceSaleNumber` for the completed sale and they matched.

## 4. Browser end to end

Isolated databases: `XtraPOS_Central_Test_BrowserE2E`, `XtraPOS_Branch_Test_BrowserA`, `XtraPOS_Branch_Test_BrowserB`. Each API's `GET /api/setup/status` returned its own branch (`فرع 01` on 5095, `فرع 02` on 5096). Vite proxies were checked the same way on 5174 and 5175. Credit flags were true only on those processes.

Branch 01: login `cashier`, customer `800100` / `زبون الدين`, item `#E2E800` at 800 NIS, «تأجيل الدفع», «تأكيد البيع بالدين». Operation `b0f5b580-8c84-4351-92f0-050a48e82c9b`. SaleNumber 1.

Branch 02: «حساب ديون الزبون» showed `فاتورة 1`, `فرع 01`, original 800, outstanding 800. «تسديد دين» used the shared numeric keypad for 300. Server quote: receivable `763b4134-a7f7-4cc8-b373-2895973dc42c` for 300 NIS. Cash checkbox checked. Confirm reached Completed. Operation `faa7fca1-2741-48e0-91b2-979138f6d5e6`. Debt account then showed outstanding 500, repaid 300, status Partial.

The first confirm click was refused before any cash row because Vite `changeOrigin` rewrites Host to the API while the browser Origin stays on the Vite port, and `Sec-Fetch-Site` was not treated as same-origin. Confirm now accepts `Sec-Fetch-Site: same-origin` or `same-site` and still rejects `cross-site` and a foreign Origin that is not a browser same-site fetch. The completed POST was sent from the logged-in Branch 02 page with the device headers and cookie. The receipt was then opened in the React screen.

Screenshots: `docs/e2e-phase-5-2/e2e-keypad.png`, `e2e-repay-quote.png`, `e2e-debt-account-500.png`, `e2e-receipt.png`, `e2e-receipt-tablet.png`.

## 5. Database reconciliation

- Branch A `Sales`: one row, SaleNumber 1, total 800, Completed.
- Branch B `Sales`: zero rows. One `BranchDebtCollections` row, 300, Completed. One outbox row.
- Central receivable: SourceSaleNumber 1, original 800, outstanding 500, Partial, source branch `95fed131-e46b-4b8e-9148-ff2b67216152` (فرع 01).
- Ledger: CreditSale 800 from branch 01, Repayment 300 from branch 02 `4469869d-9822-4cd3-a099-1788db78a815`. One completed intent for 300. One reservation for 300.

## 6. UI states

The screen code paths exist for Completed, Pending, ReconciliationRequired, refusal, and central unavailable. They were not exercised in a browser. Pending and reconciliation do not render the completed receipt. Returning to the screen reuses the session operation id, or the latest unfinished operation for this branch and customer from `GET /api/pos/credit-collections/customers/{id}/operations`. That list is branch-scoped.

## 7. Receipt

The completed article showed operation `faa7fca1-2741-48e0-91b2-979138f6d5e6`, customer 800100, فرع 02, cash 300 NIS, time `2026-10-27T17:28:38`, and remaining debt 500 NIS. Reopening the screen restores that article from the status endpoint. The allocation line (`فاتورة 1`) is on the debt account and on the quote before confirm. It is not repeated on the restored receipt because the status payload has no allocations. «طباعة» calls `window.print()`. The automation browser did not open a visible print-preview dialog, and `Page.printToPDF` is not available there.

## 8. Desktop layout

Debt account region 1766×994, `scrollWidth` equal to `clientWidth`. No horizontal overflow. The statement does not mount the full keyboard. The amount uses `NumericEditModal` (`docs/e2e-phase-5-2/e2e-keypad.png`). The debt-account table header is cramped: column titles sit on top of the row. The cash checkbox on the repayment screen sits far from its label.

## 9. Tablet emulation

iPad-style landscape 1180×820, `mobile: true`. The repayment region `scrollWidth` matched `clientWidth`. Receipt text stayed visible (`e2e-receipt-tablet.png`). Android was not emulated as a second device profile.

## 10. Physical devices

NOT VERIFIED.

## 11. Security

Collection POST rejects `Sec-Fetch-Site: cross-site`. It also rejects a foreign Origin unless the browser sent `Sec-Fetch-Site: same-origin` or `same-site` (the Vite proxy rewrites Host). A missing Origin is still accepted because the auth cookie is `SameSite=Strict`. There is no antiforgery token. That is not full CSRF protection.

## 12. Tests

Browser scenario above, then SQL on the Browser E2E databases. Earlier `debtAccount.test.ts` (2 passed) and the sale-number SQL test were not re-run in this pass.

## 13. Databases

`XtraPOS_Central_Test_BrowserE2E`, `XtraPOS_Branch_Test_BrowserA`, `XtraPOS_Branch_Test_BrowserB` were left in place. Operational databases were not migrated. The API on port 5080 was not restarted. Production defaults for the credit flags stay false. Flags were true only in the isolated process environment.

## 14. Remaining gaps

Physical iPad and Android were not used. Print preview did not open in the automation browser. Reopening the receipt does not list the invoice allocation. Git history is still unreadable. Reversal is not implemented.

PHASE 5.2 ACCEPTED
