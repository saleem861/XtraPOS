import { ChartNoAxesCombined, RefreshCw, Search, TrendingDown, TrendingUp, UserRound } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { deviceHeaders } from "../api/client";
import type { CustomerListItem } from "../api/customers";
import {
  appendPage,
  createCustomerPurchasesClient,
  starGlyphs,
  trendText,
  type PurchaseFilters,
  type PurchaseLine,
  type PurchasesPage,
} from "../api/customerPurchases";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { CustomerSearchScreen } from "./CustomerSearchScreen";
import { OrderDetailsScreen, type DebtDocument } from "./OrderDetailsScreen";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import "./employeeCashErrors.css";
import "./customerPurchases.css";

export interface PurchaseCustomerSeed {
  customerId: string;
  customerNumber?: string | null;
  name?: string | null;
}

function money(value: number | null | undefined) {
  return value == null ? "—" : value.toFixed(2);
}

function stamp(value: string | null) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString("ar-PS-u-nu-latn", { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}

/** الزبون العام لا تُعرض مشترياته (كما في النظام القديم). */
function isGeneralCustomer(number: string | null | undefined) {
  return number === "1" || number === "*";
}

export function CustomerPurchasesScreen({ session, user, initialCustomer, onBack }: {
  session: DeviceSession;
  user: CurrentUser;
  initialCustomer?: PurchaseCustomerSeed | null;
  onBack: () => void;
}) {
  const client = useMemo(() => createCustomerPurchasesClient(session), [session]);
  const [customer, setCustomer] = useState<PurchaseCustomerSeed | null>(
    initialCustomer && !isGeneralCustomer(initialCustomer.customerNumber) ? initialCustomer : null,
  );
  const [searching, setSearching] = useState(!initialCustomer || isGeneralCustomer(initialCustomer.customerNumber));
  const [filters, setFilters] = useState<PurchaseFilters>({ from: "", to: "", item: "" });
  const [page, setPage] = useState<PurchasesPage | null>(null);
  const [lines, setLines] = useState<PurchaseLine[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");
  const [order, setOrder] = useState<DebtDocument | null>(null);
  const request = useRef(0);
  const sentinel = useRef<HTMLDivElement>(null);

  const load = useCallback(async (target: PurchaseCustomerSeed, next: PurchaseFilters) => {
    const id = request.current + 1;
    request.current = id;
    setLoading(true);
    setError("");
    try {
      const result = await client.page(target.customerId, next, 0);
      if (request.current !== id) return;
      setPage(result);
      setLines(result.lines);
    } catch (caught) {
      if (request.current === id) {
        setPage(null);
        setLines([]);
        setError(caught instanceof Error ? caught.message : "تعذر تحميل مشتريات الزبون.");
      }
    } finally {
      if (request.current === id) setLoading(false);
    }
  }, [client]);

  const loadMore = useCallback(async () => {
    if (!customer || !page?.hasMore || loadingMore || loading) return;
    const id = request.current;
    setLoadingMore(true);
    try {
      const result = await client.page(customer.customerId, filters, lines.length);
      if (request.current !== id) return;
      setPage((current) => (current ? { ...current, hasMore: result.hasMore } : result));
      setLines((current) => appendPage(current, result.lines));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر تحميل المزيد.");
    } finally {
      setLoadingMore(false);
    }
  }, [client, customer, filters, lines.length, loading, loadingMore, page?.hasMore]);

  // التصفية تُطبَّق وحدها بعد لحظة من آخر تغيير.
  useEffect(() => {
    if (!customer) return undefined;
    const timer = window.setTimeout(() => void load(customer, filters), 350);
    return () => window.clearTimeout(timer);
  }, [customer, filters, load]);

  // التمرير إلى آخر الجدول يجلب الصفحة التالية؛ 50 فاتورة في كل مرة، فلا يتأخر العرض مهما كثرت.
  useEffect(() => {
    const node = sentinel.current;
    if (!node || typeof IntersectionObserver !== "function") return undefined;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) void loadMore();
    }, { rootMargin: "200px" });
    observer.observe(node);
    return () => observer.disconnect();
  }, [loadMore, lines.length]);

  async function openOrder(line: PurchaseLine) {
    const response = await fetch(`/api/pos/sales-inquiry/${line.saleId}?central=${page?.source === "central"}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setError("تعذر فتح الفاتورة.");
      return;
    }
    setOrder(await response.json() as DebtDocument);
  }

  if (order) {
    return (
      <section className="new-customer sales-review customer-purchases" dir="rtl" aria-label="تفاصيل الفاتورة">
        <ScreenHeader title="تفاصيل الفاتورة" onBack={() => setOrder(null)} />
        <div className="customer-purchases-order">
          <OrderDetailsScreen embedded title="تفاصيل الفاتورة" document={order} onClose={() => setOrder(null)} />
        </div>
      </section>
    );
  }

  const shown = page?.customer ?? (customer ? { customerId: customer.customerId, customerNumber: customer.customerNumber ?? null, name: customer.name ?? "", phone: null } : null);
  const stars = page?.stars ?? null;
  const showItem = Boolean(page?.totals.itemNumber);

  return (
    <section className="new-customer sales-review customer-purchases" dir="rtl" aria-label="مشتريات الزبون">
      <ScreenHeader title="مشتريات الزبون" context={user.branchName} onBack={searching ? undefined : onBack} />
      <div className="customer-purchases-body">
        <div className="customer-purchases-top">
          <div className="customer-purchases-who">
            <span className="customer-purchases-avatar" aria-hidden="true"><UserRound /></span>
            {shown ? (
              <div>
                <strong>{shown.name || "زبون"}</strong>
                <small>{[shown.customerNumber ? `رقم ${shown.customerNumber}` : null, shown.phone].filter(Boolean).join(" — ")}</small>
              </div>
            ) : <div><strong>لم يُختر زبون</strong><small>اختر زبوناً لعرض مشترياته</small></div>}
            <button type="button" onClick={() => setSearching(true)}><Search aria-hidden="true" />{shown ? "تغيير الزبون" : "اختيار زبون"}</button>
          </div>

          {stars ? (
            <div className={`customer-stars is-${stars.trend.toLowerCase()}`} aria-label={`${stars.stars} من 5 نجوم`}>
              <span className="customer-stars-glyphs" aria-hidden="true">{starGlyphs(stars.stars)}</span>
              <span className="customer-stars-trend">
                {stars.trend === "Up" ? <TrendingUp aria-hidden="true" /> : stars.trend === "Down" ? <TrendingDown aria-hidden="true" /> : null}
                {trendText(stars)}
              </span>
              <small>
                آخر {stars.windowDays} يوماً مقارنة بالـ {stars.windowDays} يوماً التي قبلها
                {stars.neededForNext != null && stars.stars < 5 ? ` — يلزمه ${money(stars.neededForNext)} للنجمة التالية` : ""}
              </small>
              {stars.thresholdsAreDefault ? <small className="customer-stars-default">حدود افتراضية؛ يحددها المدير من الإعدادات ← نجوم الزبائن.</small> : null}
            </div>
          ) : null}
        </div>

        <div className="customer-purchases-filters">
          <label>من<input type="date" value={filters.from} onChange={(event) => setFilters((current) => ({ ...current, from: event.target.value }))} /></label>
          <label>إلى<input type="date" value={filters.to} onChange={(event) => setFilters((current) => ({ ...current, to: event.target.value }))} /></label>
          <label>رقم الصنف<input value={filters.item} inputMode="search" placeholder="اختياري" onChange={(event) => setFilters((current) => ({ ...current, item: event.target.value }))} /></label>
          <button type="button" onClick={() => setFilters({ from: "", to: "", item: "" })}>كل الفترات</button>
          <button type="button" disabled={!customer} onClick={() => customer && void load(customer, filters)}><RefreshCw aria-hidden="true" />تحديث</button>
        </div>

        {page?.notice ? <p className="cash-errors-notice customer-purchases-notice">{page.notice}</p> : null}

        {page ? (
          <div className="customer-purchases-cards">
            <article><span>الفواتير</span><strong>{page.totals.count}</strong></article>
            {page.seeTotals ? <article><span>مجموع المشتريات</span><strong>{money(page.totals.salesTotal)}</strong></article> : null}
            {page.seeTotals && page.totals.earnTotal != null ? <article className="is-earn"><span>الأرباح</span><strong>{money(page.totals.earnTotal)}</strong></article> : null}
            {showItem ? (
              <article className="is-item">
                <span>{page.totals.itemName ?? page.totals.itemNumber}</span>
                <strong>{page.totals.itemQuantity ?? 0}</strong>
                <small>الكمية{page.seeTotals && page.totals.itemTotal != null ? ` — ${money(page.totals.itemTotal)}` : ""}</small>
              </article>
            ) : null}
          </div>
        ) : null}

        <div className="customer-purchases-results" style={{ position: "relative" }}>
          {error ? <p className="form-error">{error}</p> : null}
          {loading && !page ? <Waiting label="جاري تحميل مشتريات الزبون…" /> : null}
          {loading && page ? <Waiting variant="overlay" label="جاري التحديث…" /> : null}
          {!customer && !loading ? (
            <div className="customer-purchases-empty"><ChartNoAxesCombined aria-hidden="true" /><p>اختر زبوناً لعرض مشترياته في كل الفروع.</p></div>
          ) : null}
          {page ? (
            <div className="cash-errors-table customer-purchases-table">
              <table>
                <thead>
                  <tr>
                    <th>التاريخ</th>
                    <th>رقم الفاتورة</th>
                    <th>الفرع</th>
                    <th>النقطة</th>
                    <th>البائع</th>
                    <th>الأصناف</th>
                    {showItem ? <th>كمية الصنف</th> : null}
                    <th>الإجمالي</th>
                    {page.seeTotals && page.totals.earnTotal != null ? <th>الربح</th> : null}
                  </tr>
                </thead>
                <tbody>
                  {lines.length === 0 ? (
                    <tr><td colSpan={9} className="cash-errors-empty">لا توجد مشتريات مطابقة.</td></tr>
                  ) : lines.map((line) => (
                    <tr key={line.saleId} onClick={() => void openOrder(line)} title="فتح الفاتورة">
                      <td>{stamp(line.completedAt)}</td>
                      <td>{line.saleNumber}</td>
                      <td>{line.branchName ?? "—"}</td>
                      <td>{line.stationNumber != null ? `${line.stationNumber} ${line.stationName ?? ""}` : "—"}</td>
                      <td>{line.cashier || "—"}</td>
                      <td>{line.lineCount}</td>
                      {showItem ? <td>{line.itemQuantity ?? 0}</td> : null}
                      <td className="cash-errors-amount">{money(line.total)}</td>
                      {page.seeTotals && page.totals.earnTotal != null ? <td className="cash-errors-amount">{money(line.earn)}</td> : null}
                    </tr>
                  ))}
                </tbody>
              </table>
              <div ref={sentinel} className="customer-purchases-more">
                {loadingMore ? <Waiting compact label="جاري تحميل المزيد…" /> : null}
                {page.hasMore && !loadingMore ? <button type="button" onClick={() => void loadMore()}>عرض المزيد ({lines.length} من {page.totals.count})</button> : null}
                {!page.hasMore && lines.length > 0 ? <small>{lines.length} فاتورة</small> : null}
              </div>
            </div>
          ) : null}
        </div>
      </div>

      {searching ? (
        <CustomerSearchScreen
          session={session}
          permissions={user.permissions}
          onClose={() => { setSearching(false); if (!customer) onBack(); }}
          onChoose={(picked: CustomerListItem | null) => {
            setSearching(false);
            if (!picked) return;
            if (isGeneralCustomer(picked.customerNumber)) {
              setError("الزبون العام لا تُعرض مشترياته. اختر زبوناً مسجّلاً.");
              return;
            }
            setPage(null);
            setLines([]);
            setCustomer({ customerId: picked.customerId, customerNumber: picked.customerNumber, name: picked.name });
          }}
        />
      ) : null}
    </section>
  );
}
