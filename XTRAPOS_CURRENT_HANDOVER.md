# XtraPOS handover — Gold Market checkpoint to Nablus PC

This file records the source state on the Gold Market development machine before work continues on the Nablus branch PC.

## A. Architecture

- Central owns master data.
- Each branch owns its operational data.
- A sale on the branch must not require Central at sale time.
- The browser never talks to SQL. It talks to the branch API.
- IndexedDB (`xtrapos-pos`, version 1, store `cartDrafts`) stores cart drafts only.

## B. Ownership

- Central: items, units, assortment structure, default prices, users/roles as already modeled.
- Branch: stock movements, sales, cash sessions, branch prices, device registration, cart checkout.
- StockOwnership walks `ItemUnits`. `ParentItemID` is an assortment, not a stock item.

## C. Local ports

- Web (Vite): `http://localhost:5173`
- API: `http://localhost:5080`
- Phone on the same LAN uses the PC address and port 5173, for example `http://<this-pc-lan-ip>:5173`. Do not point the phone at `localhost`.

## D. Completed POS work in this checkpoint

- One persistent fast-entry field: رقم الصنف.
- Search, quantity, price, checkout, and shift are temporary surfaces.
- Cashier wording: بدء الوردية / الوردية مفتوحة|مغلقة / إغلاق الوردية / جرد الصندوق.
- Quantity stepper, keyboard entry, and manual unit-price / line-total override UI.
- Checkout modal with cash, card, and split. Fine pointer shows the internal keypad. Coarse pointer uses the native keyboard.
- Adaptive numeric edit modal (`NumericEditModal`) for quantity, unit price, and line total.
- Dynamic display shells for phone portrait, phone landscape, and coarse tablets below 1200px.
- Development IndexedDB panel at `?debug=idb` (development builds only).

## E. Dynamic display

- Width chooses cart presentation (container queries).
- Height chooses density.
- Primary pointer chooses touch behavior: `matchMedia("(pointer: fine)")`. Do not use `any-pointer: coarse`.
- Phone portrait (`pointer: coarse`, portrait, max-width 599px): compact navy bar, one-row summary, title and back on one row, fast entry then add/search.
- Phone landscape (`pointer: coarse`, landscape, max-height 500px): short navy bar, hidden internal title, compact total, cart is the scroller.
- Coarse pointer and width under 1200px stays on the stable touch shell. A wide landscape tablet does not become the desktop layout.
- Stable touch shell: workspace is a column at `100dvh` with overflow hidden. Header `flex: 0`. Cart `flex: 1`, `min-height: 0`, `overflow-y: auto`. Bottom actions `flex: 0`. The page is not the scroll owner.
- Non-interactive touch/pen taps do not steal focus back to fast entry. Mouse clicks on non-interactive text still do.

## F. Numeric modals

- Quantity, unit price, and line total share `NumericEditModal`.
- Fine pointer: centered modal, internal keypad, physical keyboard, first digit replaces the current value. Quantity is integer-only. Money allows two decimal places. Comma is normalized to a dot.
- Coarse pointer: no internal keypad, `inputMode="numeric"` or `"decimal"`, `type="text"` so the browser spinner stays off. The overlay follows `visualViewport` and sits at the top of the visible area.
- Checkout keypad behavior is separate and was not part of the numeric-edit refactor.

## G. IndexedDB cart restore

- The loading text «جاري استعادة السلة…» renders only when `cartRestore === "loading" || !cart` in `PosScreen`.
- A null draft is a successful restore and must create an empty cart, then `setCart` and `setCartRestore("ready")`.
- On `http://` (the LAN phone), `crypto.randomUUID()` throws. Cart and line ids use `createClientId()`, which falls back to `getRandomValues`.
- `?debug=idb` traces each StrictMode run. The open timeout stays at 3 seconds. Do not treat `db-open-success` as a finished cart restore.

## H. LAN mobile testing

- The browser calls relative `/api` and `/health`.
- Vite proxies those paths to `http://localhost:5080` on the PC.
- The API listens on `0.0.0.0:5080` in the development launch profile.
- Development CORS allows the Gold Market LAN origin used on this PC. On the Nablus PC, use that PC's own LAN address. Do not reuse `192.168.0.77`.

## I. Business rules that must stay

- Known stock may go negative.
- Unknown stock stays NULL.
- StockOwnership follows `ItemUnits`.
- Assortment parents are not stock items.
- Variable-value lines: one portion, then quantity multiplies. Weight stays per portion. One stock movement per line.
- Checkout is atomic.
- Expected cash is opening cash plus completed cash sale payments, not tendered cash and not card amounts.
- An unsold cart blocks shift close.
- Manual price override keeps the resolved price and applies the entered unit price or line total. Do not change that contract in this checkpoint.

## J. Unfinished UI task

On mobile landscape, رقم الصنف + إضافة + بحث عن صنف must sit on one compact horizontal row. Do not start this until the Nablus PC is opened from this checkpoint. Do not treat the current landscape fast-entry row as finished.

## K. First action on the Nablus PC

There is no Git remote on the Gold Market machine. Copy this repository folder, including `.git`, to the Nablus PC. Do not copy `src/XtraPOS.Api/appsettings.Development.json` over the Nablus file. Open the folder, read this handover, then `npm install` inside `src/XtraPOS.Web` and restore the API with the Nablus branch database.

## L. Warning

DO NOT overwrite Nablus branch identity or configuration with Gold Market branch-local configuration. Do not copy a PC's SQL database, device registration, or browser IndexedDB onto the other machine.

## M. Nablus PC after checkpoint e925539

The Nablus branch database on this PC is `XtraPOS_Branch_01` on `localhost` (default instance `MSSQLSERVER`). Stored identity: Business Smoker World, branch فرع نابلس, code 01, BranchID `ED75DBB5-02C8-F111-AE0B-00E18CC5C023`. Station كاونتر 1 is `EE75DBB5-02C8-F111-AE0B-00E18CC5C023`. Device صندوق 1 is `EF75DBB5-02C8-F111-AE0B-00E18CC5C023`.

Migrations `016_SalesFoundation`, `017_POSPaymentsAndCashSessions`, and `018_ManualLinePriceOverride` are applied on that database. `018` runs its column backfill through dynamic SQL so a fresh batch can compile after the columns are added. No sale was created.

Development database identity is machine-local User Secrets (`SqlServer:Server`, `SqlServer:Database`), id `8f3c1a2e-6b44-4d91-9c70-1a2b3c4d5e6f` on the API project. `appsettings.Development.json` is generic and is shared by OneDrive. It must not contain a branch database name.

Gold Market must set its own User Secrets on the Gold Market PC before starting the API again. Do not create those secrets from the Nablus PC. Gold Market must use its own local database identity.

This PC's LAN origin for Development CORS is `http://192.168.0.114:5173`. The API listens on `0.0.0.0:5080`. The web app stays on port 5173.

The unfinished landscape fast-entry row in section J is still not done.
