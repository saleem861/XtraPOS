import { ArrowDown, ArrowLeft, ArrowUp, ArrowUpDown } from "lucide-react";
import { useRef, useState } from "react";
import { createCustomerClient, type CustomerListItem } from "../api/customers";
import type { DeviceSession } from "../types/setup";
import { sortCustomers, type CustomerSortColumn } from "../cart/customerSearch";
import { FullKeyboard } from "./FullKeyboard";
import { NewCustomerScreen } from "./NewCustomerScreen";

function customerSyncLabel(state: string | null): string {
  switch (state) {
    case "Current": return "محدث";
    case "Offline": return "دون اتصال";
    case "Preparing": return "جارٍ التحضير";
    case "Failed": return "تعذرت المزامنة";
    default: return "";
  }
}

export function CustomerSearchScreen({ session, permissions, onClose, onChoose }: {
  session: DeviceSession;
  permissions: string[];
  onClose: () => void;
  onChoose: (customer: CustomerListItem | null) => void;
}) {
  const customers = createCustomerClient(session);
  const inputRef = useRef<HTMLInputElement>(null);
  const desktopKeyboard = typeof window.matchMedia === "function" && window.matchMedia("(pointer: fine) and (hover: hover)").matches;
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<CustomerListItem[]>([]);
  const [syncState, setSyncState] = useState<string | null>(null);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [done, setDone] = useState(false);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [selected, setSelected] = useState(-1);
  const [sort, setSort] = useState<{ column: CustomerSortColumn; direction: "asc" | "desc" } | null>(null);
  const [editor, setEditor] = useState<string | undefined | null>(null);
  const ordered = sort ? sortCustomers(results, sort.column, sort.direction) : results;

  async function search(nextPage = 1) {
    const term = query.trim();
    setError("");
    setMessage("");
    if (!term) {
      setResults([]);
      setDone(true);
      setSelected(-1);
      return;
    }
    setLoading(true);
    try {
      const loaded = await customers.search(term, nextPage);
      setSyncState(loaded.syncState);
      setResults(nextPage === 1 ? loaded.customers : [...results, ...loaded.customers]);
      setTotal(loaded.totalCount);
      setPage(nextPage);
      if (nextPage === 1) setSelected(-1);
      setDone(true);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر تحميل بيانات الزبائن");
      setDone(false);
    } finally {
      setLoading(false);
    }
  }

  if (editor !== null) {
    return (
      <NewCustomerScreen
        session={session}
        customerId={editor}
        permissions={permissions}
        title={editor ? "تعديل زبون" : "إضافة زبون جديد"}
        onBack={() => setEditor(null)}
        onSaved={(customerId) => {
          void customers.get(customerId).then((customer) => { if (customer) onChoose(customer); });
          setEditor(null);
        }}
      />
    );
  }

  return (
    <section className={desktopKeyboard ? "new-customer full-keyboard-screen has-keyboard" : "new-customer"} dir="rtl" aria-label="بحث عن زبون" style={{ zIndex: 50 }}>
      <header className="new-customer-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <h2>بحث عن زبون</h2>
        <button type="button" className="new-customer-back" onClick={onClose}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      <form className="customer-search-content" onSubmit={(event) => { event.preventDefault(); void search(1); }}>
        <div className="pos-search-row">
          <input ref={inputRef} autoFocus enterKeyHint="search" aria-label="بحث عن زبون" value={query} placeholder="اسم الزبون أو رقمه أو الهاتف..." onChange={(event) => { setQuery(event.target.value); setDone(false); setSort(null); }} onKeyDown={(event) => {
            if (event.key === "Enter") { event.preventDefault(); void search(1); }
            if (event.key === "ArrowDown") { event.preventDefault(); setSelected((index) => Math.min(ordered.length - 1, Math.max(-1, index) + 1)); }
            if (event.key === "ArrowUp") { event.preventDefault(); setSelected((index) => index <= 0 ? 0 : index - 1); }
          }} />
          <button className="nav-button" type="submit">عرض النتائج</button>
        </div>
        {customerSyncLabel(syncState) ? <p className="pos-search-count" aria-live="polite">{customerSyncLabel(syncState)}</p> : null}
        {done ? <p className="pos-search-count">{total} نتيجة</p> : null}
        {error ? <p className="status-error">{error}</p> : null}
        {message ? <p className="pos-search-count">{message}</p> : null}
        <div className="pos-search-table customer-search-grid" role="table" aria-label="نتائج الزبائن">
          <div className="pos-search-columns customer-search-header" role="row">
            {([
              ["number", "رقم الزبون"],
              ["name", "اسم الزبون"],
              ["phone", "الهاتف"],
              ["type", "نوع الزبون"],
              ["price", "التسعيرة"],
            ] as const).map(([column, label]) => (
              <button key={column} type="button" className="customer-search-column-header" role="columnheader" aria-sort={sort?.column === column ? (sort.direction === "asc" ? "ascending" : "descending") : "none"} onClick={() => setSort((current) => current?.column === column ? { column, direction: current.direction === "asc" ? "desc" : "asc" } : { column, direction: "asc" })}>
                {label}
                {sort?.column === column ? (sort.direction === "asc" ? <ArrowUp aria-hidden="true" /> : <ArrowDown aria-hidden="true" />) : <ArrowUpDown aria-hidden="true" />}
              </button>
            ))}
          </div>
          <div className="pos-search-body">
            {loading ? <p className="pos-search-empty">جاري البحث…</p> : null}
            {ordered.map((customer, index) => (
              <button key={customer.customerId} type="button" className={index === selected ? "customer-search-row is-selected" : "customer-search-row"} onClick={() => onChoose(customer)}>
                <span className="customer-search-cell" role="cell">{customer.customerNumber ?? "—"}</span>
                <span className="customer-search-cell" role="cell" title={customer.name}>{customer.name}</span>
                <span className="customer-search-cell" role="cell" dir="ltr">{customer.phone ?? "—"}</span>
                <span className="customer-search-cell" role="cell">{customer.classificationName ?? "—"}</span>
                <span className="customer-search-cell" role="cell" dir="ltr">{customer.priceCode ?? "—"}</span>
              </button>
            ))}
            {done && !error && !loading && ordered.length === 0 ? <p className="pos-search-empty">لا يوجد زبائن مطابقون للبحث</p> : null}
          </div>
        </div>
        {results.length < total ? <button className="nav-button" type="button" onClick={() => void search(page + 1)}>المزيد</button> : null}
        <div className="pos-customer-actions">
          <button className="nav-button" type="button" onClick={() => setEditor(undefined)}>زبون جديد</button>
          <button className="nav-button" type="button" disabled={!ordered[selected]} onClick={() => { const customer = ordered[selected]; if (customer) setEditor(customer.customerId); }}>تعديل الزبون</button>
          <button className="nav-button" type="button" disabled={!ordered[selected]} onClick={() => { const customer = ordered[selected]; if (customer) onChoose(customer); }}>حساب ديون الزبون</button>
          <button className="nav-button" type="button" onClick={() => onChoose(null)}>استخدام زبون عادي</button>
          <button className="primary-button" type="button" disabled={selected < 0 || !ordered[selected]} onClick={() => { const customer = ordered[selected]; if (customer) onChoose(customer); }}>إدخال</button>
        </div>
      </form>
      {desktopKeyboard ? <FullKeyboard onEnter={() => void search(1)} /> : null}
    </section>
  );
}
