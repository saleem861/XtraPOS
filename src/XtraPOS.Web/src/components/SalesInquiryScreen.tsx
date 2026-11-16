import { Calendar, ChevronDown, Clock, Database, FileSpreadsheet, Filter, RefreshCw, Sigma, Trash2, TrendingUp, Zap } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { deviceHeaders } from "../api/client";
import { distinctBy } from "../distinct";
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

interface InquiryBranch {
  branchId: string;
  branchName: string;
  branchCode?: string;
}

interface InquiryStation {
  stationNumber: number;
  stationName: string;
  branchId: string;
  branchName: string;
}

interface InquiryPayload {
  total: number;
  salesTotal: number;
  earnTotal?: number | null;
  seeEarns: boolean;
  origin?: string;
  unavailable?: boolean;
  message?: string;
  lines: SalesLine[];
  options: { stations: InquiryStation[]; users: InquiryUser[]; classes: string[]; branches?: InquiryBranch[] };
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
  const [stationNumber, setStationNumber] = useState(`${session.branchId}:${session.stationNumber}`);
  const [sumsView, setSumsView] = useState(false);
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
  const [origin, setOrigin] = useState("local");
  const classMenu = useRef<HTMLDetailsElement>(null);
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
  const appliedRef = useRef(filters);
  const [appliedKey, setAppliedKey] = useState(filterKey);
  const stale = filterKey !== appliedKey;
  const [seenFilter, setSeenFilter] = useState(filterKey);
  if (seenFilter !== filterKey) {
    setSeenFilter(filterKey);
    setPage(0);
  }

  function invalidRange(source = filters) {
    if (source.useDate && source.dateFrom > source.dateTo) return "تاريخ البداية أكبر من تاريخ النهاية.";
    return "";
  }

  function query(skip: number, sumsOnly: boolean, source = appliedRef.current) {
    const params = new URLSearchParams({
      sumsOnly: String(sumsOnly),
      skip: String(skip),
      take: String(pageSize),
      useDate: String(source.useDate),
      dateFrom: source.dateFrom,
      dateTo: source.dateTo,
      useTime: String(source.useTime),
      timeFrom: source.timeFrom,
      timeTo: source.timeTo,
      useStation: String(source.useStation),
      stationNumber: source.stationNumber.includes(":") ? source.stationNumber.slice(source.stationNumber.lastIndexOf(":") + 1) : source.stationNumber,
      useItem: String(source.useItem),
      matchItem: String(source.matchItem),
      itemNumber: source.itemNumber,
      useName: String(source.useName),
      itemName: source.itemName,
      useUser: String(source.useUser),
      userId: source.userId,
      useCustomer: String(source.useCustomer),
      customerNumber: source.customerNumber,
      useCustomerName: String(source.useCustomerName),
      customerName: source.customerName,
      useSaleNumber: String(source.useSaleNumber),
      saleNumber: source.saleNumber,
      useBranch: String(source.useBranch),
      branchName: source.branchName,
      useClass: String(source.useClass && source.classifications.length > 0),
      classifications: source.classifications.join("\u001e"),
      modifiedOnly: String(source.modifiedOnly),
    });
    return params;
  }

  async function load(sumsOnly: boolean, generation: number, signal: AbortSignal, source = appliedRef.current) {
    const problem = invalidRange(source);
    if (problem) {
      setMessage(problem);
      return;
    }
    setMessage("");
    const response = await fetch(`/api/pos/sales-inquiry?${query(page * pageSize, sumsOnly, source)}`, { headers: deviceHeaders(session), signal });
    if (generation !== requestId.current) return;
    if (!response.ok) {
      const problemBody = await response.json().catch(() => null) as { message?: string } | null;
      setMessage(problemBody?.message || "تعذر تحميل المبيعات.");
      return;
    }
    const payload = await response.json() as InquiryPayload;
    if (generation !== requestId.current) return;
    setOptions({
      stations: distinctBy(payload.options?.stations, (station) => `${station.branchId}:${station.stationNumber}:${station.stationName}`),
      users: distinctBy(payload.options?.users, (user) => user.userId),
      classes: distinctBy(payload.options?.classes, (name) => name),
      branches: distinctBy(payload.options?.branches, (branch) => branch.branchId || branch.branchName),
    });
    const branches = payload.options?.branches ?? [];
    if (branches.length === 1 && branchName !== branches[0].branchName) setBranchName(branches[0].branchName);
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
    setOrigin(payload.origin || "local");
    setMessage("");
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
  }, [page, reloadKey]);

  function applyFilters() {
    appliedRef.current = filters;
    setAppliedKey(filterKey);
    setSumsView(false);
    setPage(0);
    setReloadKey((value) => value + 1);
  }

  async function openSale(saleId: string) {
    const response = await fetch(`/api/pos/sales-inquiry/${saleId}?central=${origin === "central"}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تعذر فتح الطلبية.");
      return;
    }
    setOpenedOrder(await response.json() as DebtDocument);
  }

  function resetFilters() {
    setMessage("");
    const now = today();
    const next = {
      useDate: true, dateFrom: now, dateTo: now, useTime: false, timeFrom: "00:00:00", timeTo: "23:59:59",
      useItem: false, matchItem: false, itemNumber: "", useSaleNumber: false, saleNumber: "",
      useCustomerName: false, customerName: "", useBranch: false, branchName: session.branchName,
      useName: false, itemName: "", useCustomer: false, customerNumber: "",
      useStation: false, stationNumber: `${session.branchId}:${session.stationNumber}`,
      useUser: false, userId: "", useClass: false, classifications: [] as string[], modifiedOnly: false,
    };
    appliedRef.current = next;
    setAppliedKey(JSON.stringify(next));
    setSumsView(false);
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
    setBranchName(session.branchName);
    setUseName(false);
    setItemName("");
    setUseCustomer(false);
    setCustomerNumber("");
    setUseStation(false);
    setStationNumber(`${session.branchId}:${session.stationNumber}`);
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
    const params = query(0, false, filters);
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
    const encodedName = response.headers.get("X-Export-File-Name");
    const blob = await response.blob();
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = encodedName ? decodeURIComponent(encodedName) : "مبيعات.xlsx";
    link.click();
    URL.revokeObjectURL(link.href);
    setMessage(truncated ? "تم تصدير أول 5000 سطراً فقط لأن النتائج أكثر من ذلك." : "");
  }

  function toggleClass(name: string) {
    setClassifications((current) => current.includes(name) ? current.filter((item) => item !== name) : [...current, name]);
  }

  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const allClasses = options.classes.length > 0 && options.classes.every((name) => classifications.includes(name));
  const selectedBranch = options.branches?.find((branch) => branch.branchName.trim() === branchName.trim());
  const visibleStations = options.stations.filter((station) => !useBranch || station.branchId === selectedBranch?.branchId || station.branchName.trim() === branchName.trim());

  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard" : "new-customer sales-review"} dir="rtl" aria-label="مراجعة المبيعات" onKeyDown={moveToNextField}>
      <ScreenHeader title={openedOrder ? "تفاصيل الطلبية" : "مراجعة المبيعات"} onBack={openedOrder ? () => setOpenedOrder(null) : onBack} />
      <div className="sales-review-body">
        <aside className="sales-review-actions" aria-label="إجراءات سريعة">
          <h3><Zap aria-hidden="true" />إجراءات سريعة</h3>
          <div className="sales-review-action-split">
          <div className="sales-review-action-buttons">
          <button type="button" className={stale ? "is-stale" : undefined} onClick={applyFilters}><RefreshCw aria-hidden="true" />تحديث{stale ? <span className="sales-refresh-dot" aria-hidden="true" /> : null}</button>
          <button type="button" onClick={() => {
            setSumsView(true);
            const generation = ++requestId.current;
            const controller = new AbortController();
            void load(true, generation, controller.signal, filters);
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
          <h3 className="sales-review-panel-title"><Filter aria-hidden="true" />فلاتر</h3>
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
            <div className="sales-review-line is-single">
            <FilterCheck label="رقم الطلبية" checked={useSaleNumber} onChange={setUseSaleNumber}><input aria-label="رقم الطلبية" placeholder="رقم الطلبية" disabled={!useSaleNumber} value={saleNumber} onChange={(event) => setSaleNumber(event.target.value)} /></FilterCheck>
            <FilterCheck label="رقم الصنف" checked={useItem} onChange={setUseItem}><input aria-label="رقم الصنف" placeholder="رقم الصنف" disabled={!useItem} value={itemNumber} onChange={(event) => setItemNumber(event.target.value)} /></FilterCheck>
            <label className="sales-review-check"><input type="checkbox" checked={matchItem} onChange={(event) => { setMatchItem(event.target.checked); if (event.target.checked) setUseItem(true); }} />مطابقة تامة</label>
            <FilterCheck className="is-wide" label="اسم الصنف" checked={useName} onChange={setUseName}><input aria-label="اسم الصنف" placeholder="اسم الصنف" disabled={!useName} value={itemName} onChange={(event) => setItemName(event.target.value)} /></FilterCheck>
            <FilterCheck className="is-wide" label="اسم الموظف" checked={useUser} onChange={setUseUser}>
              <select aria-label="الموظف" disabled={!useUser} value={userId} onChange={(event) => setUserId(event.target.value)}>
                <option value="">اختر الموظف</option>
                {distinctBy(options.users, (user) => user.userId).map((user) => <option key={user.userId} value={user.userId}>{user.displayName}</option>)}
              </select>
            </FilterCheck>
            </div>
            <div className="sales-review-line is-four">
            <FilterCheck label="الفرع" checked={useBranch} onChange={(value) => { setUseBranch(value); if (!value) setUseStation(false); }}>
              <select aria-label="الفرع" disabled={!useBranch} value={branchName} onChange={(event) => setBranchName(event.target.value)}>
                {distinctBy(options.branches?.length ? options.branches : [{ branchId: session.branchId, branchName: session.branchName }], (branch) => branch.branchName).map((branch) => (
                  <option key={branch.branchId} value={branch.branchName}>{branch.branchName}</option>
                ))}
              </select>
            </FilterCheck>
            <FilterCheck label="النقطة" checked={useStation} disabled={!useBranch} onChange={(value) => { if (useBranch) setUseStation(value); }}>
              <select aria-label="النقطة" disabled={!useBranch || !useStation} value={stationNumber} onChange={(event) => setStationNumber(event.target.value)}>
                <option value="">اختر النقطة</option>
                {visibleStations.map((station) => <option key={`${station.branchId}-${station.stationNumber}`} value={`${station.branchId}:${station.stationNumber}`}>{station.stationNumber} — {station.stationName}</option>)}
              </select>
            </FilterCheck>
            <FilterCheck label="رقم الزبون" checked={useCustomer} onChange={setUseCustomer}><input aria-label="رقم الزبون" placeholder="رقم الزبون" disabled={!useCustomer} value={customerNumber} onChange={(event) => setCustomerNumber(event.target.value)} /></FilterCheck>
            <FilterCheck label="اسم الزبون" checked={useCustomerName} onChange={setUseCustomerName}><input aria-label="اسم الزبون" placeholder="اسم الزبون" disabled={!useCustomerName} value={customerName} onChange={(event) => setCustomerName(event.target.value)} /></FilterCheck>
            </div>
            <div className="sales-review-line is-two">
            <FilterCheck label="التصنيف" checked={useClass} onChange={setUseClass}>
              <details ref={classMenu} className="sales-review-class-menu">
                <summary>{options.classes.length === 0 ? "لا توجد تصنيفات" : classifications.length === 0 || allClasses ? "الكل" : classifications.join("، ")}</summary>
                <div className="sales-review-class-pop">
                  <button type="button" className="sales-review-class-done" onClick={() => { if (classMenu.current) classMenu.current.open = false; }}>تم</button>
                  {options.classes.length > 0 ? (
                    <label><input type="checkbox" disabled={!useClass} checked={allClasses} onChange={() => setClassifications(allClasses ? [] : [...options.classes])} />الكل</label>
                  ) : <span>لا توجد تصنيفات</span>}
                  {distinctBy(options.classes, (name) => name).map((name) => (
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
          {sumsView ? (
            <div className="sales-review-sum-panel">
              <p>مجموع المبيعات</p>
              <strong>₪ {money(salesTotal)}</strong>
              {seeEarns ? <p>الأرباح ₪ {money(earnTotal)}</p> : null}
              <span>{total} سطراً حسب الفلاتر</span>
            </div>
          ) : (
          <>
          <div className="sales-review-table">
            <table>
              <thead>
                <tr>
                  <th>عرض</th><th>رقم الطلبية</th><th>رقم الصنف</th><th>اسم الصنف</th><th>الكمية</th><th>السعر الأصلي</th><th>سعر الزبون</th><th>السعر المنفذ</th><th>الإجمالي</th>
                  {seeEarns ? <><th>التكلفة</th><th>الربح</th></> : null}
                  <th>التاريخ</th><th>اسم الزبون</th><th>رقم الزبون</th><th>النقطة</th><th>ملاحظات</th><th>تم التعديل</th>
                </tr>
              </thead>
              <tbody>
                {message ? <tr><td colSpan={seeEarns ? 17 : 15}>{message}</td></tr> : lines.length === 0 ? <tr><td colSpan={seeEarns ? 17 : 15}>لا توجد مبيعات مطابقة.</td></tr> : lines.map((line, index) => (
                  <tr key={`${line.saleId}-${index}`}>
                    <td><button type="button" className="sales-review-view" onClick={() => void openSale(line.saleId)}>عرض</button></td>
                    <td>{line.saleNumber}</td>
                    <td>{line.itemNumber}</td>
                    <td>{line.itemName}</td>
                    <td>{line.quantity}</td>
                    <td>{money(line.originalPrice)}</td>
                    <td>{money(line.customerPrice)}</td>
                    <td>{money(line.executedPrice)}</td>
                    <td>{money(line.total)}</td>
                    {seeEarns ? <><td>{money(line.cost)}</td><td className="is-earn">{money(line.earn)}</td></> : null}
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
          </>
          )}
          </div>
      </div>
      {desktop ? (
        <button type="button" className="sales-review-keyboard-toggle" onClick={() => setKeyboardOpen((value) => !value)}>
          <ChevronDown aria-hidden="true" style={{ transform: keyboardOpen ? "none" : "rotate(180deg)" }} />
          {keyboardOpen ? "إخفاء لوحة المفاتيح والأرقام" : "إظهار لوحة المفاتيح والأرقام"}
        </button>
      ) : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={applyFilters} /> : null}
      {openedOrder ? (
        <div className="sales-review-details">
          <OrderDetailsScreen embedded title="تفاصيل الطلبية" document={openedOrder} onClose={() => setOpenedOrder(null)} />
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

function FilterCheck({ label, checked, disabled, onChange, children, className }: { label: string; checked: boolean; disabled?: boolean; onChange: (value: boolean) => void; children: ReactNode; className?: string }) {
  const box = useRef<HTMLDivElement>(null);
  return (
    <div ref={box} className={`${checked ? "sales-review-filter" : "sales-review-filter is-off"}${className ? ` ${className}` : ""}`}>
      <label><input type="checkbox" checked={checked} disabled={disabled} onChange={(event) => { onChange(event.target.checked); if (event.target.checked) focusFilterField(box.current); }} />{label}</label>
      {children}
    </div>
  );
}
