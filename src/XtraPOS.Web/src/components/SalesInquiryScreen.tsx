import { Calendar, ChevronDown, Clock, Database, FileSpreadsheet, Filter, RefreshCw, Sigma, Trash2, TrendingUp, Zap } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { deviceHeaders } from "../api/client";
import type { DeviceSession } from "../types/setup";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { FullKeyboard } from "./FullKeyboard";
import { OrderDetailsScreen, type DebtDocument } from "./OrderDetailsScreen";
import { ScreenHeader } from "./ScreenHeader";

interface SalesLine {
  saleId: string;
  saleNumber: number;
  itemNumber: string;
  itemName: string;
  quantity: number;
  originalPrice: number;
  customerPrice: number;
  executedPrice: number;
  unitCost?: number | null;
  total: number;
  cost?: number | null;
  earn?: number | null;
  completedAt: string | null;
  customerName: string | null;
  customerNumber: string | null;
  cashier: string;
  stationNumber: number;
  modified: boolean;
}

interface InquiryUser {
  userId: string;
  displayName: string;
}

interface InquiryPayload {
  total: number;
  salesTotal: number;
  earnTotal?: number | null;
  seeEarns: boolean;
  unavailable?: boolean;
  message?: string;
  lines: SalesLine[];
  options: { stations: string[]; users: InquiryUser[]; classes: string[]; branches?: string[] };
}

const pageSize = 50;

function today() {
  return new Date().toLocaleDateString("en-CA", { timeZone: "Asia/Hebron" });
}

const money = (value: number | null | undefined) =>
  value == null || Number.isNaN(value) ? "—" : value.toLocaleString("en-GB", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export function SalesInquiryScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const initial = today();
  const desktop = usesDesktopCustomerKeyboard();
  const [useDate, setUseDate] = useState(true);
  const [keyboardOpen, setKeyboardOpen] = useState(true);
  const [dateFrom, setDateFrom] = useState(initial);
  const [dateTo, setDateTo] = useState(initial);
  const [useTime, setUseTime] = useState(false);
  const [timeFrom, setTimeFrom] = useState("00:00:00");
  const [timeTo, setTimeTo] = useState("23:59:59");
  const [useItem, setUseItem] = useState(false);
  const [matchItem, setMatchItem] = useState(false);
  const [itemNumber, setItemNumber] = useState("");
  const [useSaleNumber, setUseSaleNumber] = useState(false);
  const [saleNumber, setSaleNumber] = useState("");
  const [useCustomerName, setUseCustomerName] = useState(false);
  const [customerName, setCustomerName] = useState("");
  const [useBranch, setUseBranch] = useState(false);
  const [branchName, setBranchName] = useState(session.branchName);
  const [useName, setUseName] = useState(false);
  const [itemName, setItemName] = useState("");
  const [useCustomer, setUseCustomer] = useState(false);
  const [customerNumber, setCustomerNumber] = useState("");
  const [useStation, setUseStation] = useState(false);
  const [stationNumber, setStationNumber] = useState(String(session.stationNumber));
  const [useUser, setUseUser] = useState(false);
  const [userId, setUserId] = useState("");
  const [useClass, setUseClass] = useState(false);
  const [classifications, setClassifications] = useState<string[]>([]);
  const [modifiedOnly, setModifiedOnly] = useState(false);
  const [options, setOptions] = useState<InquiryPayload["options"]>({ stations: [], users: [], classes: [], branches: [] });
  const [lines, setLines] = useState<SalesLine[]>([]);
  const [total, setTotal] = useState(0);
  const [salesTotal, setSalesTotal] = useState(0);
  const [earnTotal, setEarnTotal] = useState<number | null>(null);
  const [seeEarns, setSeeEarns] = useState(false);
  const [openedOrder, setOpenedOrder] = useState<DebtDocument | null>(null);
  const [message, setMessage] = useState("");
  const [page, setPage] = useState(0);
  const [reloadKey, setReloadKey] = useState(0);
  const requestId = useRef(0);
  const filters = useMemo(() => ({
    useDate, dateFrom, dateTo, useTime, timeFrom, timeTo, useItem, matchItem, itemNumber, useSaleNumber, saleNumber,
    useCustomerName, customerName, useBranch, branchName, useName, itemName, useCustomer, customerNumber,
    useStation, stationNumber, useUser, userId, useClass, classifications, modifiedOnly,
  }), [useDate, dateFrom, dateTo, useTime, timeFrom, timeTo, useItem, matchItem, itemNumber, useSaleNumber, saleNumber, useCustomerName, customerName, useBranch, branchName, useName, itemName, useCustomer, customerNumber, useStation, stationNumber, useUser, userId, useClass, classifications, modifiedOnly]);
  const filterKey = JSON.stringify(filters);
  const [seenFilter, setSeenFilter] = useState(filterKey);
  if (seenFilter !== filterKey) {
    setSeenFilter(filterKey);
    setPage(0);
  }

  function invalidRange() {
    if (useDate && dateFrom > dateTo) return "تاريخ البداية أكبر من تاريخ النهاية.";
    return "";
  }

  function query(skip: number, sumsOnly: boolean) {
    const params = new URLSearchParams({
      sumsOnly: String(sumsOnly),
      skip: String(skip),
      take: String(pageSize),
      useDate: String(useDate),
      dateFrom,
      dateTo,
      useTime: String(useTime),
      timeFrom,
      timeTo,
      useStation: String(useStation),
      stationNumber,
      useItem: String(useItem),
      matchItem: String(matchItem),
      itemNumber,
      useName: String(useName),
      itemName,
      useUser: String(useUser),
      userId,
      useCustomer: String(useCustomer),
      customerNumber,
      useCustomerName: String(useCustomerName),
      customerName,
      useSaleNumber: String(useSaleNumber),
      saleNumber,
      useBranch: String(useBranch),
      branchName,
      useClass: String(useClass && classifications.length > 0),
      classifications: classifications.join(","),
      modifiedOnly: String(modifiedOnly),
    });
    return params;
  }

  async function load(sumsOnly: boolean, generation: number, signal: AbortSignal) {
    const problem = invalidRange();
    if (problem) {
      setMessage(problem);
      return;
    }
    setMessage("");
    const response = await fetch(`/api/pos/sales-inquiry?${query(page * pageSize, sumsOnly)}`, { headers: deviceHeaders(session), signal });
    if (generation !== requestId.current) return;
    if (!response.ok) {
      const problemBody = await response.json().catch(() => null) as { message?: string } | null;
      setMessage(problemBody?.message || "تعذر تحميل المبيعات.");
      return;
    }
    const payload = await response.json() as InquiryPayload;
    if (generation !== requestId.current) return;
    setOptions(payload.options);
    const branches = payload.options.branches ?? [];
    if (branches.length === 1 && branchName !== branches[0]) setBranchName(branches[0]);
    if (payload.unavailable) {
      setMessage(payload.message || "بيانات الفرع المحدد غير متاحة من نقطة البيع الحالية.");
      setSalesTotal(0);
      setEarnTotal(null);
      setSeeEarns(payload.seeEarns);
      setTotal(0);
      setLines([]);
      return;
    }
    setSalesTotal(payload.salesTotal);
    setEarnTotal(payload.earnTotal ?? null);
    setSeeEarns(payload.seeEarns);
    setTotal(payload.total);
    if (!sumsOnly) setLines(payload.lines ?? []);
  }

  useEffect(() => {
    const generation = ++requestId.current;
    const controller = new AbortController();
    const handle = window.setTimeout(() => {
      void load(false, generation, controller.signal).catch((error: unknown) => {
        if (error instanceof DOMException && error.name === "AbortError") return;
        if (generation === requestId.current) setMessage("تعذر تحميل المبيعات.");
      });
    }, 450);
    return () => {
      window.clearTimeout(handle);
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterKey, page, reloadKey]);

  async function openSale(saleId: string) {
    const response = await fetch(`/api/pos/sales-inquiry/${saleId}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تعذر فتح الطلبية.");
      return;
    }
    setOpenedOrder(await response.json() as DebtDocument);
  }

  function resetFilters() {
    const now = today();
    setUseDate(true);
    setReloadKey((value) => value + 1);
    setDateFrom(now);
    setDateTo(now);
    setUseTime(false);
    setTimeFrom("00:00:00");
    setTimeTo("23:59:59");
    setUseItem(false);
    setMatchItem(false);
    setItemNumber("");
    setUseSaleNumber(false);
    setSaleNumber("");
    setUseCustomerName(false);
    setCustomerName("");
    setUseBranch(false);
    setBranchName(options.branches?.[0] || session.branchName);
    setUseName(false);
    setItemName("");
    setUseCustomer(false);
    setCustomerNumber("");
    setUseStation(false);
    setStationNumber(String(session.stationNumber));
    setUseUser(false);
    setUserId("");
    setUseClass(false);
    setClassifications([]);
    setModifiedOnly(false);
  }

  async function exportExcel() {
    const problem = invalidRange();
    if (problem) {
      setMessage(problem);
      return;
    }
    const params = query(0, false);
    params.delete("sumsOnly");
    params.delete("skip");
    params.delete("take");
    const response = await fetch(`/api/pos/sales-inquiry/export?${params}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      const problemBody = await response.json().catch(() => null) as { message?: string } | null;
      setMessage(problemBody?.message || "تعذر تصدير المبيعات.");
      return;
    }
    const truncated = response.headers.get("X-Export-Truncated") === "true";
    const blob = await response.blob();
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = "sales-review.xlsx";
    link.click();
    URL.revokeObjectURL(link.href);
    setMessage(truncated ? "تم تصدير أول 5000 سطراً فقط لأن النتائج أكثر من ذلك." : "");
  }

  function toggleClass(name: string) {
    setClassifications((current) => current.includes(name) ? current.filter((item) => item !== name) : [...current, name]);
  }

  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const allClasses = options.classes.length > 0 && options.classes.every((name) => classifications.includes(name));

  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard" : "new-customer sales-review"} dir="rtl" aria-label="مراجعة المبيعات" onKeyDown={moveToNextField}>
      <ScreenHeader title="مراجعة المبيعات" onBack={onBack} />
      <div className="sales-review-body">
        <aside className="sales-review-actions" aria-label="إجراءات سريعة">
          <h3><Zap aria-hidden="true" />إجراءات سريعة</h3>
          <div className="sales-review-action-split">
          <div className="sales-review-action-buttons">
          <button type="button" onClick={() => setReloadKey((value) => value + 1)}><RefreshCw aria-hidden="true" />تحديث</button>
          <button type="button" onClick={() => {
            const generation = ++requestId.current;
            const controller = new AbortController();
            void load(true, generation, controller.signal);
          }}><Sigma aria-hidden="true" />مجموع</button>
          <button type="button" className="is-danger" onClick={resetFilters}><Trash2 aria-hidden="true" />إلغاء الفلاتر</button>
          <button type="button" className="is-excel" onClick={() => void exportExcel()}><FileSpreadsheet aria-hidden="true" />تصدير Excel</button>
          </div>
          <div className="sales-review-cards">
            <article className="is-sales">
              <Database aria-hidden="true" />
              <div><span>مجموع المبيعات</span><strong>₪ {money(salesTotal)}</strong></div>
            </article>
            {seeEarns ? (
              <article className="is-profit">
                <TrendingUp aria-hidden="true" />
                <div><span>الأرباح</span><strong>₪ {money(earnTotal)}</strong></div>
              </article>
            ) : null}
          </div>
          </div>
        </aside>
        <div className="sales-review-main">
          <h3 className="sales-review-panel-title"><Filter aria-hidden="true" />تصفية النتائج</h3>
          <div className="sales-review-when">
            <div>
              <label><input type="checkbox" checked={useDate} onChange={(event) => { setUseDate(event.target.checked); if (event.target.checked) focusFilterField(event.currentTarget.closest("div")); }} />حسب التاريخ من</label>
              <label className="sales-review-field"><Calendar aria-hidden="true" /><input type="date" aria-label="من تاريخ" disabled={!useDate} value={dateFrom} onChange={(event) => setDateFrom(event.target.value)} /></label>
              <span>إلى</span>
              <label className="sales-review-field"><Calendar aria-hidden="true" /><input type="date" aria-label="إلى تاريخ" disabled={!useDate} value={dateTo} onChange={(event) => setDateTo(event.target.value)} /></label>
            </div>
            <div>
              <label><input type="checkbox" checked={useTime} onChange={(event) => { setUseTime(event.target.checked); if (event.target.checked) focusFilterField(event.currentTarget.closest("div")); }} />حسب الوقت من</label>
              <label className="sales-review-field"><Clock aria-hidden="true" /><input type="time" step={1} aria-label="من وقت" disabled={!useTime} value={timeFrom} onChange={(event) => setTimeFrom(event.target.value)} /></label>
              <span>إلى</span>
              <label className="sales-review-field"><Clock aria-hidden="true" /><input type="time" step={1} aria-label="إلى وقت" disabled={!useTime} value={timeTo} onChange={(event) => setTimeTo(event.target.value)} /></label>
            </div>
          </div>
          <div className="sales-review-filters">
            <div className="sales-review-line">
            <FilterCheck label="رقم الطلبية" checked={useSaleNumber} onChange={setUseSaleNumber}><input aria-label="رقم الطلبية" placeholder="رقم الطلبية" disabled={!useSaleNumber} value={saleNumber} onChange={(event) => setSaleNumber(event.target.value)} /></FilterCheck>
            <FilterCheck label="رقم الصنف" checked={useItem} onChange={setUseItem}><input aria-label="رقم الصنف" placeholder="رقم الصنف" disabled={!useItem} value={itemNumber} onChange={(event) => setItemNumber(event.target.value)} /></FilterCheck>
            <label className="sales-review-check"><input type="checkbox" checked={matchItem} onChange={(event) => { setMatchItem(event.target.checked); if (event.target.checked) setUseItem(true); }} />مطابقة تامة</label>
            <FilterCheck label="اسم الصنف" checked={useName} onChange={setUseName}><input aria-label="اسم الصنف" placeholder="اسم الصنف" disabled={!useName} value={itemName} onChange={(event) => setItemName(event.target.value)} /></FilterCheck>
            <FilterCheck label="اسم الموظف" checked={useUser} onChange={setUseUser}>
              <select aria-label="الموظف" disabled={!useUser} value={userId} onChange={(event) => setUserId(event.target.value)}>
                <option value="">اختر الموظف</option>
                {options.users.map((user) => <option key={user.userId} value={user.userId}>{user.displayName}</option>)}
              </select>
            </FilterCheck>
            </div>
            <div className="sales-review-line is-four">
            <FilterCheck label="الفرع" checked={useBranch} onChange={setUseBranch}>
              <select aria-label="الفرع" disabled={!useBranch} value={branchName} onChange={(event) => setBranchName(event.target.value)}>
                <option value={options.branches?.[0] || session.branchName}>{options.branches?.[0] || session.branchName}</option>
              </select>
            </FilterCheck>
            <FilterCheck label="النقطة" checked={useStation} onChange={setUseStation}>
              <select aria-label="النقطة" disabled={!useStation} value={stationNumber} onChange={(event) => setStationNumber(event.target.value)}>
                <option value="">اختر النقطة</option>
                {options.stations.map((station) => <option key={station} value={station}>{station}</option>)}
              </select>
            </FilterCheck>
            <FilterCheck label="رقم الزبون" checked={useCustomer} onChange={setUseCustomer}><input aria-label="رقم الزبون" placeholder="رقم الزبون" disabled={!useCustomer} value={customerNumber} onChange={(event) => setCustomerNumber(event.target.value)} /></FilterCheck>
            <FilterCheck label="اسم الزبون" checked={useCustomerName} onChange={setUseCustomerName}><input aria-label="اسم الزبون" placeholder="اسم الزبون" disabled={!useCustomerName} value={customerName} onChange={(event) => setCustomerName(event.target.value)} /></FilterCheck>
            </div>
            <div className="sales-review-line is-two">
            <FilterCheck label="التصنيف" checked={useClass} onChange={setUseClass}>
              <details className="sales-review-class-menu">
                <summary>{options.classes.length === 0 ? "لا توجد تصنيفات" : classifications.length === 0 || allClasses ? "الكل" : classifications.join("، ")}</summary>
                <div className="sales-review-class-pop">
                  {options.classes.length > 0 ? (
                    <label><input type="checkbox" disabled={!useClass} checked={allClasses} onChange={() => setClassifications(allClasses ? [] : [...options.classes])} />الكل</label>
                  ) : <span>لا توجد تصنيفات</span>}
                  {options.classes.map((name) => (
                    <label key={name}><input type="checkbox" disabled={!useClass} checked={classifications.includes(name)} onChange={() => toggleClass(name)} />{name}</label>
                  ))}
                </div>
              </details>
            </FilterCheck>
            <label className="sales-review-check"><input type="checkbox" checked={modifiedOnly} onChange={(event) => setModifiedOnly(event.target.checked)} />السعر المعدل فقط</label>
            </div>
          </div>
        </div>
          <div className="sales-review-results">
          <div className="sales-review-table">
            <table>
              <thead>
                <tr>
                  <th>رقم الطلبية</th><th>رقم الصنف</th><th>اسم الصنف</th><th>الكمية</th><th>السعر الأصلي</th><th>سعر الزبون</th><th>السعر المنفذ</th><th>الإجمالي</th>
                  <th>التاريخ</th><th>اسم الزبون</th><th>رقم الزبون</th><th>النقطة</th><th>ملاحظات</th><th>تم التعديل</th>
                </tr>
              </thead>
              <tbody>
                {message ? <tr><td colSpan={14}>{message}</td></tr> : lines.length === 0 ? <tr><td colSpan={14}>لا توجد مبيعات مطابقة.</td></tr> : lines.map((line, index) => (
                  <tr key={`${line.saleId}-${index}`} onDoubleClick={() => void openSale(line.saleId)}>
                    <td><button type="button" className="sales-review-order" onClick={() => void openSale(line.saleId)}>{line.saleNumber}</button></td>
                    <td>{line.itemNumber}</td>
                    <td>{line.itemName}</td>
                    <td>{line.quantity}</td>
                    <td>{money(line.originalPrice)}</td>
                    <td>{money(line.customerPrice)}</td>
                    <td>{money(line.executedPrice)}</td>
                    <td>{money(line.total)}</td>
                    <td>{line.completedAt ? new Date(line.completedAt).toLocaleDateString("en-GB", { timeZone: "Asia/Hebron" }) : "—"}</td>
                    <td>{line.customerName || "—"}</td>
                    <td>{line.customerNumber || "—"}</td>
                    <td>{line.stationNumber}</td>
                    <td>—</td>
                    <td className={line.modified ? "is-yes" : "is-no"}>{line.modified ? "نعم" : "لا"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="sales-review-pager">
            <span>الصفحة {page + 1} من {pageCount} — {total} سطراً</span>
            <button type="button" disabled={page <= 0} onClick={() => setPage((value) => Math.max(0, value - 1))}>السابق</button>
            <button type="button" disabled={(page + 1) * pageSize >= total} onClick={() => setPage((value) => value + 1)}>التالي</button>
          </div>
          </div>
      </div>
      {desktop ? (
        <button type="button" className="sales-review-keyboard-toggle" onClick={() => setKeyboardOpen((value) => !value)}>
          <ChevronDown aria-hidden="true" style={{ transform: keyboardOpen ? "none" : "rotate(180deg)" }} />
          {keyboardOpen ? "إخفاء لوحة المفاتيح والأرقام" : "إظهار لوحة المفاتيح والأرقام"}
        </button>
      ) : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={() => setReloadKey((value) => value + 1)} /> : null}
      {openedOrder ? (
        <div className="sales-review-details">
          <OrderDetailsScreen title="تفاصيل الطلبية" document={openedOrder} onClose={() => setOpenedOrder(null)} />
        </div>
      ) : null}
    </section>
  );
}

function moveToNextField(event: KeyboardEvent<HTMLElement>) {
  if (event.key !== "Enter") return;
  const target = event.target;
  if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement)) return;
  if (target.closest(".sales-review-table") || target.closest(".sales-review-actions")) return;
  const fields = [...event.currentTarget.querySelectorAll<HTMLElement>("input, select, textarea")].filter((field) => {
    if (field.closest(".sales-review-table") || field.closest(".sales-review-actions")) return false;
    return !field.hasAttribute("disabled");
  });
  const index = fields.indexOf(target);
  const next = fields[index + 1];
  if (!next) return;
  event.preventDefault();
  event.stopPropagation();
  next.focus();
}

function focusFilterField(container: HTMLElement | null) {
  window.setTimeout(() => {
    container?.querySelector<HTMLElement>("input:not([type=checkbox]):not([disabled]), select:not([disabled]), summary")?.focus();
  }, 0);
}

function FilterCheck({ label, checked, onChange, children }: { label: string; checked: boolean; onChange: (value: boolean) => void; children: ReactNode }) {
  const box = useRef<HTMLDivElement>(null);
  return (
    <div ref={box} className={checked ? "sales-review-filter" : "sales-review-filter is-off"}>
      <label><input type="checkbox" checked={checked} onChange={(event) => { onChange(event.target.checked); if (event.target.checked) focusFilterField(box.current); }} />{label}</label>
      {children}
    </div>
  );
}
