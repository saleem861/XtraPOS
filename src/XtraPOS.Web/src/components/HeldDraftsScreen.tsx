import { useEffect, useMemo, useRef, useState } from "react";
import { createHeldSaleClient, HeldSaleClientError, type HeldSaleSummary } from "../api/heldSales";
import { dateRangeError, draftRange, hebronToday } from "../cart/heldDraftFilters";
import type { DeviceSession } from "../types/setup";
import { ArrowLeft } from "lucide-react";
import { FullKeyboard } from "./FullKeyboard";

export function HeldDraftsScreen({
  session,
  desktopKeyboard,
  restoring,
  guardMessage,
  onClose,
  onRestore,
  onSaveThenRestore,
  onDiscardThenRestore,
  onCancelGuard,
}: {
  session: DeviceSession;
  desktopKeyboard: boolean;
  restoring: boolean;
  guardMessage: string | null;
  onClose: () => void;
  onRestore: (saleId: string) => void;
  onSaveThenRestore: () => void;
  onDiscardThenRestore: () => void;
  onCancelGuard: () => void;
}) {
  const client = useMemo(() => createHeldSaleClient(session), [session]);
  const today = hebronToday();
  const searchRef = useRef<HTMLInputElement>(null);
  const [customer, setCustomer] = useState("");
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(today);
  const [preset, setPreset] = useState<"today" | "yesterday" | "week" | "month" | "custom">("today");
  const [rows, setRows] = useState<HeldSaleSummary[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const selected = rows.find((row) => row.saleId === selectedId) ?? null;

  async function load(nextCustomer = customer, nextFrom = from, nextTo = to) {
    const invalid = dateRangeError(nextFrom, nextTo);
    if (invalid) {
      setError(invalid);
      return;
    }
    setLoading(true);
    setError("");
    try {
      const page = await client.list(nextCustomer, nextFrom, nextTo);
      setRows(page.sales);
      setSelectedId((current) => page.sales.some((row) => row.saleId === current) ? current : null);
    } catch (caught) {
      setRows([]);
      setError(caught instanceof HeldSaleClientError ? caught.message : "تعذر تحميل المحفوظات.");
    } finally {
      setLoading(false);
      searchRef.current?.focus();
    }
  }

  useEffect(() => {
    void load(customer, from, to);
    searchRef.current?.focus();
    // The screen loads today's drafts once when it opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function applyPreset(next: "today" | "yesterday" | "week" | "month") {
    const range = draftRange(next, hebronToday());
    setPreset(next);
    setFrom(range.from);
    setTo(range.to);
    void load(customer, range.from, range.to);
  }

  function reset() {
    const range = draftRange("today", hebronToday());
    setCustomer("");
    setPreset("today");
    setFrom(range.from);
    setTo(range.to);
    void load("", range.from, range.to);
  }

  return (
    <section className={desktopKeyboard ? "new-customer full-keyboard-screen has-keyboard held-drafts" : "new-customer held-drafts"} dir="rtl" aria-label="استرجاع المسودات المحفوظة">
      <header className="new-customer-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <h2>استرجاع المسودات المحفوظة</h2>
        <button type="button" className="new-customer-back" onClick={onClose}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      <div className="held-drafts-content">
        <p className="held-drafts-note">افتراضياً يتم عرض المسودات المحفوظة اليوم</p>
        <form className="held-drafts-filters" onSubmit={(event) => { event.preventDefault(); setPreset("custom"); void load(); }}>
          <label>ابحث برقم الزبون / اسم الزبون
            <input ref={searchRef} aria-label="ابحث برقم الزبون / اسم الزبون" value={customer} onChange={(event) => setCustomer(event.target.value)} />
          </label>
          <label>من تاريخ<input aria-label="من تاريخ" type="date" value={from} onChange={(event) => { setFrom(event.target.value); setPreset("custom"); }} /></label>
          <label>إلى تاريخ<input aria-label="إلى تاريخ" type="date" value={to} onChange={(event) => { setTo(event.target.value); setPreset("custom"); }} /></label>
          <button className="primary-button" type="submit">بحث</button>
          <button className="nav-button" type="button" onClick={() => { setCustomer(""); void load("", from, to); }}>مسح البحث</button>
          <button className="nav-button" type="button" onClick={reset}>إعادة تعيين</button>
        </form>
        <div className="held-drafts-presets" role="group" aria-label="فترات سريعة">
          {(["today", "yesterday", "week", "month"] as const).map((item) => (
            <button key={item} type="button" className={preset === item ? "is-selected" : ""} onClick={() => applyPreset(item)}>
              {item === "today" ? "اليوم" : item === "yesterday" ? "أمس" : item === "week" ? "آخر 7 أيام" : "آخر 30 يوم"}
            </button>
          ))}
        </div>
        <div className="held-drafts-main">
          <section className="held-drafts-results">
            <p>{loading ? "جاري التحميل…" : `${rows.length} مسودة`}</p>
            {error ? <p className="status-error">{error}<button type="button" onClick={() => void load()}>إعادة المحاولة</button></p> : null}
            {!loading && !error && rows.length === 0 ? <p>لا توجد مسودات مطابقة.</p> : null}
            <div className="held-drafts-table" role="table">
              <div className="held-drafts-row is-head" role="row">
                {["رقم المسودة", "التاريخ والوقت", "رقم الزبون", "اسم الزبون", "عدد الأصناف", "إجمالي الفاتورة", "آخر تعديل بواسطة", "ملاحظات", "استرجاع"].map((label) => <span key={label}>{label}</span>)}
              </div>
              <div className="held-drafts-scroll">
                {rows.map((row) => (
                  <div key={row.saleId} className={row.saleId === selectedId ? "held-drafts-row is-selected" : "held-drafts-row"} role="row" onClick={() => setSelectedId(row.saleId)}>
                    <span>{row.saleNumber}</span>
                    <span>{formatStamp(row.createdAt)}</span>
                    <span>{row.customerNumber || "—"}</span>
                    <span title={row.customerName || ""}>{row.customerName || "بدون عميل"}</span>
                    <span>{row.lineCount}</span>
                    <span>{row.total} ₪</span>
                    <span>{row.updatedBy || "—"}</span>
                    <span>{row.notes || "—"}</span>
                    <button type="button" disabled={restoring} onClick={(event) => { event.stopPropagation(); setSelectedId(row.saleId); onRestore(row.saleId); }}>استرجاع</button>
                  </div>
                ))}
              </div>
            </div>
          </section>
          <aside className="held-drafts-detail" aria-label="تفاصيل المسودة المحددة">
            <h3>تفاصيل المسودة المحددة</h3>
            <dl>
              <div><dt>رقم المسودة</dt><dd>{selected?.saleNumber ?? "—"}</dd></div>
              <div><dt>التاريخ والوقت</dt><dd>{selected ? formatStamp(selected.createdAt) : "—"}</dd></div>
              <div><dt>رقم الزبون</dt><dd>{selected?.customerNumber || "—"}</dd></div>
              <div><dt>اسم الزبون</dt><dd>{selected?.customerName || "—"}</dd></div>
              <div><dt>عدد الأصناف</dt><dd>{selected?.lineCount ?? "—"}</dd></div>
              <div><dt>إجمالي الفاتورة</dt><dd>{selected ? `${selected.total} ₪` : "—"}</dd></div>
              <div><dt>آخر تعديل بواسطة</dt><dd>{selected?.updatedBy || "—"}</dd></div>
              <div><dt>ملاحظات</dt><dd>{selected?.notes || "—"}</dd></div>
            </dl>
            {guardMessage ? (
              <div className="held-drafts-guard">
                <p>{guardMessage}</p>
                <button className="primary-button" type="button" onClick={onSaveThenRestore}>حفظ ثم استعادة</button>
                <button className="danger-button" type="button" onClick={onDiscardThenRestore}>تجاهل الفاتورة الحالية</button>
                <button className="nav-button" type="button" onClick={onCancelGuard}>إلغاء</button>
              </div>
            ) : null}
            <button className="primary-button" type="button" disabled={!selected || restoring} onClick={() => { if (selected) onRestore(selected.saleId); }}>استرجاع المسودة المحددة</button>
          </aside>
        </div>
      </div>
      {desktopKeyboard ? <FullKeyboard onEnter={() => void load()} /> : null}
    </section>
  );
}

function formatStamp(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat("ar-EG", { timeZone: "Asia/Hebron", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" }).format(date);
}
