import { ChevronDown } from "lucide-react";
import { useEffect, useMemo, useState, type KeyboardEvent } from "react";
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
}

interface SubmitResult {
  outcome: "Saved" | "Retry" | "NeedJustification";
  attempt: number;
  status: "Matched" | "Shortage" | "Surplus";
  difference: number;
  countId: string | null;
  charged: boolean;
  message: string;
}

export function CashDrawerScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const [denominations, setDenominations] = useState<Denomination[]>([]);
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(1);
  const [parentCountId, setParentCountId] = useState<string | null>(null);
  const [previous, setPrevious] = useState<SubmitResult | null>(null);
  const [justification, setJustification] = useState("");
  const [askJustification, setAskJustification] = useState(false);
  const [message, setMessage] = useState("");
  const [done, setDone] = useState("");
  const [shiftOpen, setShiftOpen] = useState(true);
  const [pending, setPending] = useState(false);
  const [focus, setFocus] = useState(0);
  const desktop = useDrawerKeyboard();
  const [keyboardOpen, setKeyboardOpen] = useState(desktop);
  const counted = useMemo(
    () => denominations.reduce((sum, item) => sum + (Number(quantities[item.denominationId] || "0") * item.unitValue), 0),
    [denominations, quantities],
  );

  useEffect(() => {
    void fetch("/api/cash-drawer/context", { headers: deviceHeaders(session) })
      .then(async (response) => {
        if (!response.ok) throw new Error("تعذر فتح جرد الجرار.");
        return response.json() as Promise<{
          shiftOpen: boolean;
          attempt: number;
          parentCountId: string | null;
          previousStatus: SubmitResult["status"] | null;
          previousDifference: number | null;
          denominations: Denomination[];
        }>;
      })
      .then((payload) => {
        setShiftOpen(payload.shiftOpen);
        setDenominations(payload.denominations);
        setAttempt(payload.attempt);
        setParentCountId(payload.parentCountId);
        if (payload.previousStatus && payload.previousDifference != null) {
          setPrevious({
            outcome: "Retry",
            attempt: 1,
            status: payload.previousStatus,
            difference: payload.previousDifference,
            countId: payload.parentCountId,
            charged: false,
            message: "العد الأول لم يطابق. أعد العد.",
          });
        }
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
        setPrevious(payload);
        setMessage(payload.message);
        window.setTimeout(() => document.querySelector<HTMLElement>("[data-drawer-field='justification']")?.focus(), 0);
        return;
      }
      if (payload.outcome === "Retry") {
        setAttempt(2);
        setParentCountId(payload.countId);
        setPrevious(payload);
        setQuantities({});
        setJustification("");
        setAskJustification(false);
        setMessage(payload.message);
        return;
      }
      setDone(payload.message);
    } finally {
      setPending(false);
    }
  }

  const showStatus = previous && (attempt === 1 || askJustification);
  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard" : "new-customer sales-review"} dir="rtl" aria-label="جرد الجرار" onKeyDown={move}>
      <ScreenHeader title="جرد الجرار" onBack={onBack} />
      {done ? (
        <div className="drawer-done">
          <strong>{done}</strong>
          <button className="primary-button" type="button" onClick={onBack}>رجوع</button>
        </div>
      ) : (
        <div className="sales-review-body drawer-body">
          <aside className="sales-review-actions drawer-side">
            <article><span>المحاولة</span><strong>{attempt === 1 ? "العد الأول" : "العد الثاني"}</strong>{attempt === 2 ? <small>العد الأول لم يطابق. أعد العد.</small> : null}</article>
            <article><span>المجموع المعدود</span><strong>₪ {money(counted)}</strong></article>
            {showStatus && previous ? <StatusCard result={previous} /> : null}
            {askJustification && previous?.status === "Shortage" ? <p className="drawer-charge">النقص سيُحسب على الموظف فور التأكيد.</p> : null}
            {askJustification ? (
              <label className="drawer-note">تبرير الموظف
                <textarea data-drawer-field="justification" value={justification} onChange={(event) => setJustification(event.target.value)} rows={4} />
              </label>
            ) : null}
            {message ? <p className="form-error">{message}</p> : null}
            {!shiftOpen ? <p className="form-error">افتح الوردية قبل جرد الجرار.</p> : null}
            <button className="primary-button" type="button" disabled={pending || !shiftOpen || (askJustification && justification.trim().length === 0)} onClick={() => void confirm()}>تأكيد العد</button>
            <button className="ghost-button drawer-clear" type="button" onClick={() => setQuantities({})}>مسح الكميات</button>
          </aside>
          <div className="drawer-grid">
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
        <button type="button" className="sales-review-keyboard-toggle" onClick={() => setKeyboardOpen((value) => !value)}>
          <ChevronDown aria-hidden="true" style={{ transform: keyboardOpen ? "none" : "rotate(180deg)" }} />
          {keyboardOpen ? "إخفاء لوحة المفاتيح والأرقام" : "إظهار لوحة المفاتيح والأرقام"}
        </button>
      ) : null}
      {desktop && keyboardOpen && !done ? <FullKeyboard onEnter={moveFromPad} /> : null}
    </section>
  );
}

function StatusCard({ result }: { result: SubmitResult }) {
  const kind = result.status === "Matched" ? "is-match" : result.status === "Shortage" ? "is-short" : "is-over";
  const label = result.status === "Matched" ? "مطابق" : result.status === "Shortage" ? "نقص" : "زيادة";
  return (
    <article className={kind}>
      <span>الحالة</span>
      <strong>{label}{result.status === "Matched" ? "" : ` ₪ ${money(Math.abs(result.difference))}`}</strong>
    </article>
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
