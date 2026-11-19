import { ChevronDown } from "lucide-react";
import { useEffect, useState, type KeyboardEvent } from "react";
import { deviceHeaders } from "../api/client";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";
import { FullKeyboard } from "./FullKeyboard";
import { ScreenHeader } from "./ScreenHeader";

interface Denomination {
  denominationId: string;
  unitValue: number;
  displayName: string;
  sortOrder: number;
  isActive: boolean;
}

const empty = { denominationId: "", unitValue: "", displayName: "", sortOrder: "", isActive: true };

export function CashDenominationScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const [rows, setRows] = useState<Denomination[]>([]);
  const [form, setForm] = useState(empty);
  const [message, setMessage] = useState("");
  const desktop = usesDesktopCustomerKeyboard() || (typeof window.matchMedia === "function" && window.matchMedia("(orientation: landscape) and (min-width: 900px)").matches);
  const [keyboardOpen, setKeyboardOpen] = useState(desktop);

  async function load() {
    const response = await fetch("/api/cash-drawer/denominations", { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تعذر قراءة فئات النقد.");
      return;
    }
    setRows(await response.json() as Denomination[]);
  }

  useEffect(() => { void load(); }, [session]);

  function move(event: KeyboardEvent<HTMLElement>) {
    if (event.key !== "Enter") return;
    const fields = [...document.querySelectorAll<HTMLElement>("[data-denom-field]")];
    const index = fields.indexOf(event.target as HTMLElement);
    if (index < 0) return;
    if (fields[index].dataset.denomField === "active") {
      setForm((current) => ({ ...current, isActive: !current.isActive }));
    }
    if (fields[index].dataset.denomField === "save") return;
    event.preventDefault();
    fields[index + 1]?.focus();
  }

  async function save() {
    setMessage("");
    const response = await fetch("/api/cash-drawer/denominations", {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({
        denominationId: form.denominationId || null,
        unitValue: Number(form.unitValue),
        displayName: form.displayName,
        sortOrder: Number(form.sortOrder),
        isActive: form.isActive,
      }),
    });
    const payload = await response.json().catch(() => null) as { message?: string } | null;
    if (!response.ok) {
      setMessage(payload?.message || "تعذر حفظ الفئة.");
      return;
    }
    setForm(empty);
    setMessage("تم الحفظ.");
    await load();
    document.querySelector<HTMLElement>("[data-denom-field='value']")?.focus();
  }

  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard" : "new-customer sales-review"} dir="rtl" aria-label="فئات النقد" onKeyDown={move}>
      <ScreenHeader title="فئات النقد" onBack={onBack} />
      <div className="sales-review-body drawer-body drawer-config">
        <form className="sales-review-actions drawer-side" onSubmit={(event) => { event.preventDefault(); void save(); }}>
          <h3>{form.denominationId ? "تعديل الفئة" : "فئة جديدة"}</h3>
          <label>القيمة<input data-denom-field="value" inputMode="decimal" value={form.unitValue} onChange={(event) => setForm({ ...form, unitValue: event.target.value.replace(/[^\d.]/g, "") })} /></label>
          <label>الاسم<input data-denom-field="name" value={form.displayName} onChange={(event) => setForm({ ...form, displayName: event.target.value })} /></label>
          <label>الترتيب<input data-denom-field="sort" inputMode="numeric" value={form.sortOrder} onChange={(event) => setForm({ ...form, sortOrder: event.target.value.replace(/\D/g, "") })} /></label>
          <label className="check-line"><input data-denom-field="active" type="checkbox" checked={form.isActive} onChange={(event) => setForm({ ...form, isActive: event.target.checked })} /><span>{form.isActive ? "فعّالة" : "موقوفة"}</span></label>
          {message ? <p>{message}</p> : null}
          <button data-denom-field="save" className="primary-button" type="submit">حفظ</button>
          <button className="ghost-button" type="button" onClick={() => setForm(empty)}>إلغاء</button>
        </form>
        <div className="sales-review-table">
          <table>
            <thead><tr><th>القيمة</th><th>الاسم</th><th>الترتيب</th><th>الحالة</th><th></th></tr></thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.denominationId} className={row.isActive ? "is-active-denom" : "is-stopped-denom"}>
                  <td>{row.unitValue}</td>
                  <td>{row.displayName}</td>
                  <td>{row.sortOrder}</td>
                  <td>{row.isActive ? "فعّالة" : "موقوفة"}</td>
                  <td><button type="button" onClick={() => setForm({ denominationId: row.denominationId, unitValue: String(row.unitValue), displayName: row.displayName, sortOrder: String(row.sortOrder), isActive: row.isActive })}>تعديل</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
      {desktop ? (
        <button type="button" className="sales-review-keyboard-toggle" onClick={() => setKeyboardOpen((value) => !value)}>
          <ChevronDown aria-hidden="true" style={{ transform: keyboardOpen ? "none" : "rotate(180deg)" }} />
          {keyboardOpen ? "إخفاء لوحة المفاتيح والأرقام" : "إظهار لوحة المفاتيح والأرقام"}
        </button>
      ) : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={() => {
        const fields = [...document.querySelectorAll<HTMLElement>("[data-denom-field]")];
        const index = fields.indexOf(document.activeElement as HTMLElement);
        const current = fields[index];
        if (!current) return;
        if (current.dataset.denomField === "active") setForm((value) => ({ ...value, isActive: !value.isActive }));
        if (current.dataset.denomField === "save") { void save(); return; }
        fields[index + 1]?.focus();
      }} /> : null}
    </section>
  );
}
