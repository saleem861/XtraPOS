import { useEffect, useMemo, useState, type KeyboardEvent } from "react";
import { deviceHeaders } from "../api/client";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";
import { FullKeyboard } from "./FullKeyboard";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import { KeyboardToggle, useFullKeyboardVisible } from "./keyboardVisibility";

interface Denomination {
  denominationId: string;
  unitValue: number;
  displayName: string;
  sortOrder: number;
}

interface SubmitResult {
  outcome: "Saved" | "Retry" | "NeedJustification";
  attempt: number;
  status: "Matched" | "Shortage" | "Surplus";
  difference: number;
  counted?: number;
  countId: string | null;
  charged: boolean;
  message: string;
}

export function CashDrawerScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const [denominations, setDenominations] = useState<Denomination[]>([]);
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(1);
  const [parentCountId, setParentCountId] = useState<string | null>(null);
  const [firstCounted, setFirstCounted] = useState<number | null>(null);
  const [justification, setJustification] = useState("");
  const [askJustification, setAskJustification] = useState(false);
  const [message, setMessage] = useState("");
  const [done, setDone] = useState<SubmitResult | null>(null);
  const [shiftOpen, setShiftOpen] = useState(true);
  const [pending, setPending] = useState(false);
  const [focus, setFocus] = useState(0);
  const desktop = useDrawerKeyboard();
  const [keyboardOpen] = useFullKeyboardVisible();
  const columns = 3;
  const rows = Math.max(1, Math.ceil(denominations.length / columns));
  const counted = useMemo(
    () => denominations.reduce((sum, item) => sum + (Number(quantities[item.denominationId] || "0") * item.unitValue), 0),
    [denominations, quantities],
  );

  useEffect(() => {
    void fetch("/api/cash-drawer/context", { headers: deviceHeaders(session) })
      .then(async (response) => {
        if (!response.ok) throw new Error("تعذر فتح جرد الجرار.");
        return response.json() as Promise<{ shiftOpen: boolean; denominations: Denomination[] }>;
      })
      .then((payload) => {
        setShiftOpen(payload.shiftOpen);
        setDenominations(payload.denominations);
        setAttempt(1);
        setParentCountId(null);
        setFirstCounted(null);
      })
      .catch((error: Error) => setMessage(error.message));
  }, [session]);

  function fieldOrder() {
    return [...document.querySelectorAll<HTMLElement>("[data-drawer-field='qty'], [data-drawer-field='justification']")]
      .filter((field) => field.getClientRects().length > 0);
  }

  function move(event: KeyboardEvent<HTMLElement>) {
    if (event.key !== "Enter") return;
    const fields = fieldOrder();
    const index = fields.indexOf(event.target as HTMLElement);
    if (index < 0) return;
    event.preventDefault();
    fields[(index + 1) % fields.length]?.focus();
  }

  function moveFromPad() {
    const fields = fieldOrder();
    const active = document.activeElement as HTMLElement | null;
    const index = active ? fields.indexOf(active) : focus;
    const next = fields[(Math.max(index, 0) + 1) % Math.max(fields.length, 1)];
    next?.focus();
    if (next) setFocus(fields.indexOf(next));
  }

  async function confirm() {
    setMessage("");
    setPending(true);
    try {
      const response = await fetch("/api/cash-drawer/counts", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({
          parentCountId: attempt === 2 ? parentCountId : null,
          justification: askJustification ? justification : null,
          lines: denominations.map((item) => ({ denominationId: item.denominationId, quantity: Number(quantities[item.denominationId] || "0") })),
        }),
      });
      const payload = await response.json() as SubmitResult & { message?: string };
      if (!response.ok) {
        setMessage(payload.message || "تعذر حفظ الجرد.");
        return;
      }
      if (payload.outcome === "NeedJustification") {
        setAskJustification(true);
        setMessage(payload.message);
        window.setTimeout(() => document.querySelector<HTMLElement>("[data-drawer-field='justification']")?.focus(), 0);
        return;
      }
      if (payload.outcome === "Retry") {
        setAttempt(2);
        setParentCountId(payload.countId);
        setFirstCounted(payload.counted ?? counted);
        setQuantities({});
        setJustification("");
        setAskJustification(false);
        setMessage("");
        return;
      }
      setDone(payload);
    } finally {
      setPending(false);
    }
  }

  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard" : "new-customer sales-review"} dir="rtl" aria-label="جرد الجرار" onKeyDown={move}>
      <ScreenHeader title="جرد الجرار" onBack={onBack} />
      {done ? (
        <div className="drawer-done">
          <strong>تم الجرد</strong>
          <span>المجموع المعدود ₪ {money(done.counted ?? 0)}</span>
          <span>النتيجة: {done.status === "Matched" ? "مطابق" : done.status === "Shortage" ? "نقص" : "زيادة"}</span>
          <span>الفارق ₪ {money(Math.abs(done.difference))}</span>
          {done.charged ? <p className="drawer-charge">النقص يُخصم على الموظف.</p> : null}
          <button className="primary-button" type="button" onClick={onBack}>رجوع</button>
        </div>
      ) : (
        <div className="sales-review-body drawer-body">
          <aside className="sales-review-actions drawer-side">
            {attempt === 2 ? (
              <div className="drawer-counts">
                <article><span>العد الأول</span><strong>₪ {money(firstCounted ?? 0)}</strong></article>
                <p className="drawer-recount">المجموع المعدود لا يطابق الرصيد المتوقع. أعد العد.</p>
                <article><span>العد الثاني</span><strong>₪ {money(counted)}</strong></article>
              </div>
            ) : (
              <article><span>العد الأول</span><strong>₪ {money(counted)}</strong></article>
            )}
            {askJustification ? (
              <label className="drawer-note">تبرير الموظف
                <textarea data-drawer-field="justification" value={justification} onChange={(event) => setJustification(event.target.value)} rows={2} />
              </label>
            ) : null}
            {message ? <p className="form-error">{message}</p> : null}
            {!shiftOpen ? <p className="form-error">افتح الوردية قبل جرد الجرار.</p> : null}
            <button className="primary-button" type="button" disabled={pending || !shiftOpen || (askJustification && justification.trim().length === 0)} onClick={() => void confirm()}>تأكيد العد</button>
            <button className="ghost-button drawer-clear" type="button" onClick={() => setQuantities({})}>مسح الكميات</button>
          </aside>
          <div className="drawer-grid" style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${rows}, minmax(0, 1fr))` }}>
            {denominations.map((item, index) => (
              <article key={item.denominationId} className={focus === index ? "is-focus" : undefined}>
                <strong>{item.displayName}</strong>
                <input
                  data-drawer-field="qty"
                  inputMode="numeric"
                  aria-label={`كمية ${item.displayName}`}
                  value={quantities[item.denominationId] ?? ""}
                  onFocus={() => setFocus(index)}
                  onChange={(event) => setQuantities((current) => ({ ...current, [item.denominationId]: event.target.value.replace(/\D/g, "") }))}
                />
                <span>القيمة ₪ {money(Number(quantities[item.denominationId] || "0") * item.unitValue)}</span>
              </article>
            ))}
          </div>
        </div>
      )}
      {desktop && !done ? (
        <KeyboardToggle />
      ) : null}
      {desktop && keyboardOpen && !done ? <FullKeyboard onEnter={moveFromPad} /> : null}
      {pending ? (
        <div className="drawer-wait">
          <Waiting compact label="جاري حفظ العد" />
        </div>
      ) : null}
    </section>
  );
}

function money(value: number) {
  return value.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function useDrawerKeyboard() {
  const [enabled, setEnabled] = useState(usesDesktopCustomerKeyboard() || landscapeTablet());
  useEffect(() => {
    const media = window.matchMedia("(pointer: fine) and (hover: hover), (orientation: landscape) and (min-width: 900px)");
    const update = () => setEnabled(media.matches);
    update();
    media.addEventListener("change", update);
    return () => media.removeEventListener("change", update);
  }, []);
  return enabled;
}

function landscapeTablet() {
  return typeof window.matchMedia === "function"
    && window.matchMedia("(orientation: landscape) and (min-width: 900px)").matches;
}
