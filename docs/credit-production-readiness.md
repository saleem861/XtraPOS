# Credit production readiness

Assessment: NOT READY FOR DEPLOYMENT

This is an audit of the current development tree. No operational migration was applied. No credit flag was turned on. Port 5080 was not restarted. No commit was made.

## Release matrix

| Component | Implemented | Tested | Evidence | Remaining risk | Severity | Action | Blocking |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Central credit limit and one customer balance | Yes | Isolated tests and browser E2E | Phases 2.3 through 5.3; debt account showed 800 NIS after the latest reversal | Operational databases do not have the credit schema | Critical | Apply the documented migration plan only after a verified backup | Yes |
| Credit sale, outbox, recovery | Yes | API tests | `CreditSaleIntegrationTests`, outbox lease migration 026 | Recovery of an already staged sale is not gated by the flag, which is intended | High | Keep the flag off until the variable-weight gap is closed | Yes |
| Variable-weight credit sale | Partial | Cash checkout only | Credit HTTP omits the cash-checkout expected fields | A credit sale does not reject a stale weight, price, or stock conversion the way cash checkout does | Critical | Send and compare the same expected fields | Yes |
| Cross-branch collection and cashbox | Yes | Browser plus SQL on test databases | Phase 5.2 and 5.3 | Collection receipt still shows the balance at collection time after a later reversal | Medium | Label that figure as the balance on the receipt, and show the live balance separately | No |
| Reversal and restored balance | Yes | Browser and SQL | Preview and notice showed 800 NIS, not 1100 | Posting logic is accepted and must stay unchanged | Low | None for the 1100 defect | No |
| Desktop shared keyboard | Yes | Measured | 356 px of 1080 px, 33 percent, shared `FullKeyboard` | None on the measured screen | Low | Keep the 33/67 rule | No |
| Tablet input | Code matches the media query | Emulated coarse pointer, no hover, touch | iPad 1180×820 and Android 1280×800: no full keyboard, no horizontal overflow | Physical devices not tested. A native on-screen keyboard was not opened | Medium | Physical pass before activation | No |
| HTTP authorization | Partial | Prior HTTP tests | Cookie is HttpOnly and SameSite=Strict. Collect and reverse check Origin | Credit-sale POST has no Origin check. Missing Origin is still allowed when the trusted-origin list is empty | High | Same Origin policy on every financial POST before activation | Yes |
| Printing | Screen articles exist | Browser rendering only | Invoice, receipt, and reversal notice rendered in the page | No print-preview dialog was captured | Medium | Capture one preview of each document | No |
| Source control | Not recoverable as Git | Placeholder confirmed | `.git/index` is offline (`A O P`). HEAD is `master` | A reset or index rebuild can drop uncommitted work | Critical | Copy the tree outside OneDrive and create a new repository from that copy | Yes |
| Operational flags | Off by default | Config absent | `GetValue(..., false)` and no appsettings override | Isolated test processes were started with flags true. They are not the operational API | Low | Do not copy those process settings | No |

## 1. Source and Git

`.git/index` is still an offline OneDrive placeholder. `.git/HEAD` is readable and points at `refs/heads/master`. The index was not rebuilt.

`C:\XtraPOS-phase52-snapshot` exists and is not a Git repository. It does not contain `reversalPreview.ts`.

`C:\XtraPOS-phase53-work` exists and is not a Git repository. It does not contain the current `src\XtraPOS.Web\src\credit\reversalPreview.ts`. It is an older source copy, not the current tree.

A safe recovery is a file copy of `src`, `tests`, and `docs` to a disk that is not OneDrive, then `git init` in that copy. Do not replace the OneDrive index and do not copy an older snapshot over the current tree. There is no commit until that new repository can show status and a diff.

## 2. Ledger and cashbox

The accepted lifecycle is one central receivable per credit sale, collections and reversals as new rows, and an append-only ledger. Original sale, collection, and allocation rows are not rewritten. Cash from a collection belongs to the collecting branch. A refund reversal and a recording correction use different cash effects. Net cash of a collected amount and its reversal is zero. Idempotent replay uses the operation id. A conflicting replay becomes `ReconciliationRequired`.

Existing cash checkout was not modified by this audit. The credit-sale writer calls `CheckoutService.PrepareCreditAsync` and `PersistCreditAsync`, so fixed-price lines share the cash pricing path.

## 3. Variable-weight products

Cash checkout sends `expectedFinalSellingPrice`, `expectedReferenceBasePrice`, `expectedWeightKg`, and `expectedStockOwnerQuantity`. `CheckoutService.Compare` rejects a difference only when the client sent a value. A missing value is skipped.

The credit screen sends `enteredScaleValue` and `lineType: VariableValueByWeight`. It does not send those four expected fields. `CreditSaleLine` and `CreditLineBody` have no properties for them, and `CreditSaleDocumentWriter` does not copy them onto `CheckoutLine`.

The server still prices the line with `VariableValuePricing.ResolveAsync` and rejects an invoice total that does not match that price. Stock conversion is calculated on the server. What is missing is the same client confirmation cash checkout requires. A stale scale preview can be posted on credit without the price-change rejection cash checkout would return.

Smallest correction: add the four expected fields to the credit request and map them onto `CheckoutLine` before `PrepareCreditAsync`. Do not change cash checkout.

Credit sales are not ready while this differs.

## 4. Desktop and tablet

Desktop, measured earlier on the reversal reason: shared `FullKeyboard`, 33 percent of the screen. The completed notice does not keep it. The fixed reversal amount does not open a numeric keypad.

This audit set authentic emulation on `http://127.0.0.1:5176/`: pointer coarse, hover none, `maxTouchPoints` 5, iPad landscape 1180×820, then Android landscape 1280×800. `(pointer: fine) and (hover: hover)` was false. The shared full keyboard was absent. Horizontal overflow was false. The debt account loaded **الدين القائم 800 NIS**. `visualViewport` exists. A native operating-system keyboard was not shown, so inset behavior was not observed. Physical iPad and Android remain pending. Emulation was cleared afterward.

## 5. Security

Login cookie is HttpOnly and SameSite=Strict. Device headers are required. Credit actions require `Credit.Create`, `Credit.Collect`, `Credit.Reverse`, or `Credit.View`, plus an open cash session where the financial action needs one. Monetary totals are recomputed on the server.

Collection confirm and reversal confirm reject `Sec-Fetch-Site: cross-site` and a foreign Origin that is not same-site. When `TrustedOrigins` is empty, a request with no Origin is allowed. Phase 5.1 recorded that this is not a general antiforgery token. Credit-sale POST in `CreditEndpoints.cs` does not check Origin or Fetch Metadata at all.

Before activation, credit-sale POST needs the same Origin rule as collection and reversal, and a missing Origin on a browser financial POST should be rejected once the POS origin is known.

## 6. Print and receipts

Browser rendering, not a print dialog:

- Reversal notice: customer 800200, branch 02, collection `7844495b-f7f9-4ef0-b5a1-835f218bf943`, reversal `1a4d9899-745f-4acd-8cd6-c9ef46bf9dfb`, 300 NIS, resulting debt 800 NIS, completed.
- Collection receipt for that collection still says remaining 500 NIS. That is the balance when the collection completed. The live debt account then showed 800 NIS. The receipt screen label «الدين القائم 500 NIS» can be read as the current balance.
- Credit-sale invoice printing was not opened in this audit.

No invented sale number was seen on the reversal notice. It uses the collection and reversal ids. Print preview was not captured.

## 7. Migration plan

Do not execute this against an operational database.

Central order, from `CentralMigrationRunner`: `001` through `010`, then `011_CustomerCredit`, `012_CreditCollections`, `013_ReceivableSaleNumber`, `014_CreditReversals`.

Branch order, from `MigrationRunner`: the existing chain through `023`, then `024_CreditPermissions`, `025_CreditSaleOutbox`, `026_CreditOutboxLease`, `027_DebtCollections`, `028_CollectionReversals`.

Targets: central migration files only on the central database. Branch files only on each branch database. `013` is central. `027` and `028` are branch.

Preflight: read `dbo.DatabaseSchemaHistory` on each database and stop if an id in this list is already present with a different checksum, or if a credit table exists without its history row.

Backup: full backup of central and every branch, restore each backup to a scratch instance, and run the migration there before the operational run. Keep the backups until the first business day of credit use has been reconciled.

Verification: history contains `014_CreditReversals` on central and `028_CollectionReversals` on every branch. Expected tables exist. A read-only count of sales and payments is unchanged.

Roll-forward: the runners skip ids already in history. A failed batch inside one migration file must be restored from backup. Posted credit rows are not reversible by dropping tables.

API compatibility: an old API against a migrated database can ignore the new tables. A new API against an unmigrated database fails closed when credit is disabled and fails when credit is enabled. Deploy the schema before any flag is turned on. Pending outbox rows on the old API do not include credit. During deployment, leave all three flags false so no new credit, collection, or reversal is accepted. Already staged rows, if any exist only in test databases, are recovered by the worker without the flag.

## 8. Backup and restore

No operational backup was taken in this audit. The scratch restore in section 7 is a release gate, not an optional check.

## 9. Performance

Indexes exist for customer receivables by due date, the ledger by customer and time, active authorizations, and collection intents by customer. No load test was run. Locking is the existing SQL transaction around authorization and posting. Deadlock handling was not retested here. Outbox polling is the existing worker. Large histories and multi-branch contention are not measured. Do not load-test live databases.

## 10. Monitoring

Unresolved credit sales are listed by the credit-sale status endpoint for users with `Credit.View`. `Pending` and `ReconciliationRequired` are stored states. There is no separate alert or cash-session rule that blocks closing a session because a collection is unresolved. That should be checked before activation.

## 11. Physical devices

Not verified.

## 12. Release blockers

1. Variable-weight credit sales do not carry the cash-checkout expected price, weight, and stock fields.
2. The OneDrive Git index is unreadable, and neither snapshot is a Git repository of the current tree.
3. Credit-sale POST has no Origin check.
4. Operational schema is not migrated, and it must not be migrated until the backup restore gate passes.
5. Credit flags stay false. Turning them on is out of scope.

## 13. Recommended order

1. Copy the current tree off OneDrive and prove `git status` there.
2. Add the four variable-weight expected fields to the credit sale request and cover them with one API test.
3. Apply the collection Origin check to credit-sale POST.
4. Relabel the collection receipt balance so it cannot be read as the post-reversal balance.
5. Run the migration on scratch restores.
6. Only then consider a controlled branch with flags still off, then one flag at a time.

Operational defaults remain false because the keys are absent and the code default is false. The process on port 5080 was not restarted and was not given these flags.

NOT READY FOR DEPLOYMENT

## Phase 6.1

A readable Git copy is at `C:\XtraPOS-phase61-repo`. It was cloned from the object database, not by rebuilding the OneDrive index. HEAD is `53e60c4`. After the current tree was copied onto it, `git status` lists the uncommitted work, including `reversalPreview.ts`, and the new index is a normal file. A second copy is at `C:\XtraPOS-phase61-backup`. Checksums of the credit endpoint files match across the OneDrive tree, the repository, and the backup. Neither older snapshot was used as the source. No commit was made. The OneDrive `.git/index` was not changed. There is still no remote.

Variable-weight credit lines now send and require `expectedFinalSellingPrice`, `expectedReferenceBasePrice`, `expectedWeightKg`, and `expectedStockOwnerQuantity`. Missing or different values are refused before a sale is written. Cash checkout's comparison was not changed. The authenticated HTTP test accepted a 20 NIS weighed line, rejected a missing weight, a wrong reference price, and a wrong stock quantity, recorded one stock movement, stored a 20 NIS receivable, and kept the replay to one movement.

Financial POSTs for credit sales, collection quotes, collection confirms, and reversals use `FinancialRequestGuard`. A missing Origin is rejected. A cross-site fetch is rejected. An origin is accepted only when it is the API's own origin or an exact configured origin. A same-site subdomain is not accepted automatically. The same HTTP test covered a missing Origin, an untrusted same-site Origin, cross-site metadata, and `http://127.0.0.1:5176` as a configured proxy origin. A request with a trusted Origin and no fetch-metadata header is allowed. No antiforgery token was added.

A collection receipt now labels «الرصيد عند التسديد» from the quote captured when that payment completed, and «الدين القائم الآن» from the later summary. Reloading an old receipt does not have a stored historical balance, so that figure stays blank instead of being replaced by the current balance. Posting was not changed.

`CreditHttpAuthenticationTests` and `CreditCollectionHttpTests`: 9 passed. `CreditReversalTests` passed in the preceding run.

## Phase 6.1.1 browser sale

The recovered repository `C:\XtraPOS-phase61-repo` still has a readable index. HEAD remains `53e60c4`. `git status` works. No commit was made.

Isolated API: `http://127.0.0.1:5098`, credit sales enabled only there. Web: `http://127.0.0.1:5177`, `XTRAPOS_API` pointed at 5098. Setup status through that proxy returned فرع 02. Databases: `XtraPOS_Branch_Test_BrowserB` and `XtraPOS_Central_Test_BrowserE2E`. Port 5080 was not restarted.

The cashier logged in through the POS, selected customer 800200, and added scale value 20. The cart showed Scale fixture, 2000 g, price 20 NIS. «تأجيل الدفع» showed invoice 20 NIS, deferred 20 NIS, and expected debt 820 NIS. The browser request body contained `expectedFinalSellingPrice` 20, `expectedReferenceBasePrice` 10, `expectedWeightKg` 2, and `expectedStockOwnerQuantity` 2. Those values came from the cart, not from an edited request.

SQL: sale `B04AFB84-6A49-4D87-87D6-C0358AAE5859`, sale number 1, total 20, completed. One line: scale 20, reference price 10, weight 2 kg, final price 20, stock quantity 2. One stock movement. No cash payment. One central receivable of 20. Replaying operation `7a01384a-3ef3-46f6-a9b3-b99a02b69529` returned Completed and left the sale, movement, and receivable at one each.

Negative weight, price, and stock cases remain the earlier authenticated HTTP tests. They were not repeated as extra browser sales.

Evidence: `docs/evidence/weight-credit-cart.png`, `docs/evidence/weight-credit-quote.png`.

PHASE 6.1 ACCEPTED
