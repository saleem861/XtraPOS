# XtraPOS — Sales Review, Collections, and Credit UI Status

Document date: 10 November 2026 (Asia/Hebron).
Audience: a later engineer or Gemini reading this file alone.
Scope: work done in the local repository `D:\Google\XtraPOS` and published to the staging site only. This is a status and behavior record. It is not a deployment runbook and it does not contain passwords, connection strings, or private keys.

## 1. What this document is

This file records:

- the old Windows POS behavior that the new Sales Review screen is meant to follow
- the new web POS behavior that was actually built
- the collection and credit-sale UI fixes published in the same period
- the staging releases that contain that work
- the limits that are still true after the last publish

The last staging release confirmed in this work is `20261109-220810-2969d0d`. The git label on that build is `2969d0d`. The deploy builds the files on disk, so the published site includes local edits that are not necessarily committed. Do not assume `git HEAD` equals the staging files.

The site under test is `https://test-pos.sharbelfashion.com`. Production was not changed.

After every staging publish the browser must be hard-refreshed. A normal reload can keep the previous web bundle.

## 2. Two systems

### 2.1 Old system

The old POS is a Windows VB.NET application. The files used as the behavior reference are:

- `D:\XtraposSource\XtraPOS\SalesFormArabic.vb`
- `D:\XtraposSource\XtraPOS\SalesFormArabic.designer.vb`
- `D:\XtraposSource\XtraPOS\OrdersFormArabic.vb`

The old sales screen is a read-only line report, not one row per invoice. The old Shop1 table `dbo.Sales` stores one row per sold line. That table was studied from a pasted `CREATE TABLE` script. The live Shop1 database was not queried and must not be queried. Do not import from `saleemPOSDB`, `Shop1`, `Shop2`, or `swstore`.

Old columns and how they were understood:

| Old Shop1 column | Meaning used for the new screen |
| --- | --- |
| OrderID | Sale / order number. The same number repeats once per line. |
| OriginalPrice | Original unit price. |
| CustomerPrice | Customer unit price. |
| UnitPrice | Executed unit price. |
| Price | Line total. |
| UnitCost | Unit cost. |
| Cost | Line cost. |
| Earn | Line profit. |
| SalesDate, SalesTime | When the line was sold. |
| CustomerName | Customer name stored on the old line. |
| CustomerPhoneNumber | What the old “customer number” filter searched. |
| UserShortName | Cashier short name. |
| POSNumber | Station number. |
| Comments | Free-text note. |
| IsModified | Whether the price was modified. |

Old screen behavior that was kept as the target:

- Opening sales from the order form shows the sales line report and sets the station filter to the current station. A today-only filter in the old code is commented out, so the new screen does not force “today” only because of that dead code. The later written specification did ask for today’s date to start enabled, and that is what the new screen does.
- Filters are independent checkboxes. An unchecked filter is not applied.
- Totals are calculated over the whole filtered set, not over the visible page.
- A “sums only” action refreshes totals and does not wipe the grid in the new screen. The old button cleared the grid. The new specification explicitly keeps the grid.
- Double-click or the order number opens the order. Closing that view returns to the same filters.
- Closing the report does not change the open cart.
- Profit and cost are visible only when the old `CanSeeEarns` flag is true. The product owner defined that as Admin and Manager / Supervisor only.
- The old item search had a special case for item 1300 and internal units. The new screen must not hard-code item 1300. Related units come from item-unit links.
- The old screen asked for confirmation around 300 rows. The new screen uses paging instead of that dialog.
- The old customer-number filter searched the phone column. The new screen searches `CustomerNumber`.

### 2.2 New system

XtraPOS is a branch web POS.

- Web: React and Vite in `src/XtraPOS.Web`. Local dev URL `http://127.0.0.1:5173`.
- API: .NET 8 in `src/XtraPOS.Api`. Local URL `http://127.0.0.1:5080`.
- The dev server proxies `/api` and `/health` to the API.
- A sale is a header in `Sales` plus lines in `SaleItems`. The review screen still shows one row per line, ordered by completion time descending, then sale number descending, then line number.
- Line money fields used by the review are `UnitPrice`, `LineTotal`, `ReferenceBasePrice`, `FinalSellingPrice`, `UnitCostSnapshot`, `LineCostSnapshot`, and `PriceOverrideMode`.
- There is no comments column on the new sale line. The notes column displays an em dash. Notes are not invented and no migration was added to fake history.
- Customer name on the new report is a live join to the customer, not a snapshot taken at sale time.
- Currency on this screen is shekel (`₪`), including where an earlier mockup showed a different currency.
- “A day” for the screen’s default date uses the `Asia/Hebron` timezone. SQL row time conversion uses `Israel Standard Time`, which is the timezone already used by other queries. Those two are not the same setting.
- One device reads one branch database. Asking for another branch returns an Arabic message that the selected branch is not available from the current station. It does not query central sales and it must not present partial totals as a complete other-branch report.
- The phrase `متصل بخادم الفرع` is an existing connection label and must not be edited.
- A customer must never be shown as a GUID.

Staging databases are the branch database `XtraPOS_Branch_Test_Staging` and the central database `XtraPOS_Central_Test_Staging`. Local development still points at its own branch database. Pending local migrations were not applied as part of this work. Central production data was not written.

## 3. Sales Review screen

### 3.1 Where it lives

- API: `src/XtraPOS.Api/Endpoints/SalesInquiryEndpoints.cs`
- Screen: `src/XtraPOS.Web/src/components/SalesInquiryScreen.tsx`
- Layout: `src/XtraPOS.Web/src/styles/app.css`, classes beginning with `sales-review`
- The POS button `المبيعات` is in `src/XtraPOS.Web/src/components/PosScreen.tsx`. It opens the review and is enabled. Closing it only sets the review closed. It does not clear the cart.
- Order details reuse `OrderDetailsScreen`. The details layer stays above the review, so filters, page, and scroll are not reset. The order document does not include cost. Existing print on that screen is unchanged. Print was not verified in a browser.

The screen is a separate fixed layer over the POS, using the same full-screen pattern as the other customer screens (`new-customer`). An earlier publish drew it inside the sale screen because `.sales-review` forced `position: relative`. That override was removed. Release `20261109-213205-2969d0d` is the one that made it an independent screen.

### 3.2 Who can see cost and profit

The API requires `POS.Sell` to open the inquiry. Sales totals stay visible to those users.

Cost and profit are included only for Admin, Manager, or a role whose name is Supervisor / مشرف, or for the permission `System.Administer`. For everyone else the API omits `unitCost`, `cost`, `earn`, and `earnTotal` from the JSON and from the Excel file. The Profit card is absent. The columns تكلفة الوحدة, التكلفة, and الربح are absent. Hiding them with CSS is not the rule.

### 3.3 Filters

On open:

- Date filter is on. From and to are today in `Asia/Hebron`.
- Time filter is off. The time fields still show `00:00:00` to `23:59:59`.
- Station filter is off, and the station number shown is the current device station.
- Branch filter is off. The branch shown is the current device branch.
- The first page loads immediately. Page size is 50.
- Totals come from SQL over the full filter, not from the 50 visible lines.

Clear Filters returns that same starting state and runs the query again.

Date and time rules:

- Date alone is a range of calendar days.
- Time alone is a time-of-day window.
- Date and time together are a time-of-day window on each selected day, not one continuous timestamp from the first date-time to the last date-time.
- An overnight window such as 22:00 to 02:00 is an OR of the two parts of the night.
- A reversed date range is not queried. The Arabic message is `تاريخ البداية أكبر من تاريخ النهاية.`

Other filters, each with its own checkbox:

- Item number. Exact match uses the sale-line snapshot. Without exact match, related units are included through `ItemUnits`, `InternalUnitItemID`, and `ParentItemID`. There is no hard-coded item 1300.
- Item name: partial match.
- Employee: the list shows display names and the query filters by `UserID`.
- Sale number: exact match inside the current business and branch.
- Customer number: `CustomerNumber`, partial match.
- Customer name: partial match.
- Type and classification both write the same classification list. Type is a single compact dropdown. Classification is a closed dropdown that opens a multi-select with الكل. Empty classifications show `لا توجد تصنيفات` inside the normal control height. The filter is sent only when the classification checkbox is on and at least one class is selected. Classes combine with OR inside the list and with AND against the other filters.
- Station dropdown.
- Modified prices only: `PriceOverrideMode` other than `None`. No extra input.
- Branch dropdown. Another branch is refused with `بيانات الفرع المحدد غير متاحة من نقطة البيع الحالية.`

Changing a filter reloads after a short debounce and cancels the previous request. The Update button reloads. The Sum button recalculates totals and does not clear the table.

### 3.4 Table

Columns in right-to-left order:

1. رقم الطلبية
2. رقم الصنف
3. اسم الصنف
4. الكمية
5. السعر الأصلي
6. سعر الزبون
7. السعر المنفذ
8. تكلفة الوحدة, only if allowed
9. الإجمالي
10. التكلفة, only if allowed
11. الربح, only if allowed
12. التاريخ
13. الوقت
14. رقم الزبون
15. اسم الزبون
16. النقطة
17. الموظف
18. ملاحظات, always an em dash
19. تم التعديل

The header is dark navy, sticky, with white Arabic labels. Rows alternate white and pale blue. Profit values are green when shown. The order number opens the order. Empty and error text are rows inside the table, so the header stays visible. The table is the area that grows. Filters do not grow.

Paging is previous / next. Export is a real `.xlsx` file named `sales-review.xlsx`, with an Arabic header and a metadata sheet for range, branch, and creation time. The export cap is 5000 lines. If the cap is hit, the screen says `تم تصدير أول 5000 سطراً فقط لأن النتائج أكثر من ذلك.`

### 3.5 Layout after the last visual corrections

The approved structure is:

- the existing XtraPOS header, title `مراجعة المبيعات`, not a second header
- quick actions fixed on the right, about 182 pixels wide
- under those buttons, the sales total card and, only when allowed, the profit card
- one date-and-time row
- four compact filter rows
- the sales table filling every remaining pixel above the keyboard
- the existing shared Arabic keyboard and numeric pad

Desktop and laptop, defined as fine pointer and hover, show that shared keyboard at 33 percent of the application viewport height. The keyboard component was not redesigned. Tablet and iPad keep the system keyboard and do not force the desktop keyboard.

Quick actions, and only these, are:

- تحديث and إغلاق on one row
- مجموع
- إلغاء الفلاتر
- تصدير Excel

Checkboxes are 15 pixels and sit beside their labels. An earlier bug put the checkbox above the label because the shared `.new-customer label` style is a column. `.sales-review label` forces a row. The date and time groups sit together from the right. They are not pushed to opposite ends of the row.

The filter section is a fixed-height grid: date/time about 42 pixels, then four rows of about 34 pixels. Only `.sales-review-results` has `flex: 1`.

## 4. Collection and credit UI published in the same period

These are separate from the report, but they were finished and published before the report redesign.

### 4.1 Repayment screen

- The numeric pad on credit deferral and on collection matches the sales pad height. An empty quick-action grid reserves the height of three sales quick rows.
- The repayment screen has a history table from `/api/pos/credit-history`.
- Enter on the customer number looks the customer up and refreshes debts, then focus moves to the next field.
- Enter moves to the next field on debt and collection screens. The sales screen itself is excluded from that global Enter behavior.
- When the collection screen opens, focus is the amount field `مبلغ التسديد`.
- The English API sentence `The payment exceeds the collectible balance.` is shown as an Arabic dialog with a close button: `المبلغ المدفوع يتجاوز الرصيد القابل للتحصيل.`
- The duplicate back button is hidden while credit, collection, or sales review is open, because those screens have their own header.

### 4.2 Customer search

From the sale screen, collection search uses the existing POS customer search. From Home, Customers, Debts, the search button opens `CustomerSearchScreen`, which copies that same layout. It does not import `PosScreen`, because that import would be circular. Walk-in clears the collection customer. New and edit open the existing new-customer screen.

### 4.3 Collection without a cash shift

Home → Customers → Debts can collect without an open cash shift. The request sends `allowWithoutSession: true` and a null cash session. POS collection still requires an open shift.

Migration `041_OptionalCollectionSession` makes `BranchDebtCollections.CashSessionID` nullable. Staging has this migration. Office collections store NULL. They are not attached to an open drawer.

### 4.4 Sale-screen buttons

The quick button `الديون` was removed. `تسديد` sits beside `تأجيل الدفع`. The four-column order is:

1. رمز العضوية، حفظ، طباعة ومشاركة، المحفوظات
2. إيصال، استرجاع، المبيعات، الجرار
3. تأجيل الدفع، تسديد، شاشة، فتح
4. مبيعات الزبون، الجرد

`الجرد` stays disabled. `المبيعات` opens Sales Review.

### 4.5 Cart after a posted credit sale

A credit sale is posted before the print dialog `تم ترحيل الفاتورة بنجاح`. Closing that dialog must keep the posted sale and clear the cart and the customer.

The bug was a race. The screen saved an empty cart and then called the walk-in customer change. That change still held the old cart and wrote the lines back. The fix saves one new empty cart id and resets the walk-in flags locally. It does not call `useWalkInCustomer()` or `changeCustomer(null)` after that save. Release `20261109-185524-2969d0d`.

### 4.6 Credit refusal that was explained and not changed

A screenshot showed available credit 100, limit 100, outstanding 0, order 45, and the message `تم رفض البيع بالدين. السلة ما زالت موجودة.`

That generic sentence is the fallback when the decision has no message. A missing branch `CustomerCreditPolicies` row returns `CreditDisabled` with a null available amount. The cards can still show the central policy, because policy reading uses the local row when it exists and otherwise the remote policy. A real limit failure would show the Arabic maximum-deferred sentence. This mismatch was not given a code fix.

## 5. Staging releases from this work

Older releases are listed so a later reader can see order. The site always runs the newest activated release, not all of them.

| Release | What it contained |
| --- | --- |
| `20261108-230648-2969d0d` | Pad height, repayment history, Enter lookup, Enter-to-next, duplicate back removed, empty repayment from the customers menu |
| `20261108-231804-2969d0d` | Customer search from the debts menu matches the POS search |
| `20261108-232453-2969d0d` | Menu collection without a cash shift, migration 041 |
| `20261109-184033-2969d0d` | Arabic over-limit dialog, amount focus, sale quick-button reorder |
| `20261109-185524-2969d0d` | Closing the print dialog clears the cart and does not restore lines |
| `20261109-191609-2969d0d` | First Sales Review and the enabled المبيعات button |
| `20261109-202834-2969d0d` | Sales Review specification: per-day time, overnight OR, related units, paging, xlsx, profit omitted from the API |
| `20261109-213205-2969d0d` | Sales Review is a full independent screen over the POS |
| `20261109-214644-2969d0d` | Compact four-row filters, classification dropdown, table fills the space above the keyboard |
| `20261109-215940-2969d0d` | Summary cards moved under quick actions, smaller checkboxes |
| `20261109-220810-2969d0d` | Checkboxes beside their labels, date and time groups packed to the right |

Web tests inside those deploys: 46 files, 213 tests passed. That does not prove the screen was clicked in a browser. Browser and print verification of Sales Review were not completed.

## 6. What is still true and should not be “fixed” by guessing

- Notes cannot be recovered for old or new lines. Do not add a comments column unless new sales actually store a comment, and do not backfill invented notes.
- Customer name can change after the sale because it is a live join.
- Other-branch sales reporting is not implemented.
- The credit-policy mismatch in section 4.6 is unexplained only in the sense that the cause is known and the code was left as it was.
- Local branch migrations through 041 were not applied to the local development database.
- Nothing in this work was committed unless a later explicit commit exists. The working tree was dirty on purpose. Untracked files that must not be deleted include `src/XtraPOS.Api/Catalog/StagingCatalogSeed.cs`, central migrations `024_MembershipCounterCode.sql` and `025_MembershipCounterDay.sql`, `src/XtraPOS.Api/Membership/MembershipCalendar.cs`, and `src/XtraPOS.Web/src/membership/savedIdentity.ts`.
- Catalog counts on staging at the time of these deploys were about 2814 items and 385 units. Those figures were copied earlier. Do not restore invented items.
- Another workstream owns customer classifications, price-code dropdowns, discount defaults, and central connectivity. This sales work did not replace that work.

## 7. Files a reader should open first

- `src/XtraPOS.Web/src/components/SalesInquiryScreen.tsx`
- `src/XtraPOS.Web/src/styles/app.css` at `.sales-review`
- `src/XtraPOS.Api/Endpoints/SalesInquiryEndpoints.cs`
- `src/XtraPOS.Web/src/components/PosScreen.tsx` for the المبيعات button, collection handoff, and print-dialog cart clear
- `src/XtraPOS.Web/src/components/PosCollectionScreen.tsx`
- `src/XtraPOS.Web/src/components/CustomersMenuScreen.tsx`
- `src/XtraPOS.Web/src/components/CustomerSearchScreen.tsx`
- `src/XtraPOS.Api/Data/Migrations/041_OptionalCollectionSession.sql`
- `src/XtraPOS.Api/Credit/CreditCollectionService.cs`

Old behavior reference, read only:

- `D:\XtraposSource\XtraPOS\SalesFormArabic.vb`
- `D:\XtraposSource\XtraPOS\SalesFormArabic.designer.vb`
- `D:\XtraposSource\XtraPOS\OrdersFormArabic.vb`
