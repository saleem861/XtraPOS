import { useEffect, useRef, useState } from "react";
import { OrderDetailsScreen, type DebtDocument } from "./OrderDetailsScreen";
import { deviceHeaders } from "../api/client";
import { createCustomerClient, type CreditPolicy, type CustomerListItem } from "../api/customers";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";
import { PosNumberPad } from "./PosNumberPad";
import { ScreenHeader } from "./ScreenHeader";
import "./debtScreens.css";

interface Movement {
  operationId: string;
  eventType: string;
  amount: number;
  signedEffect: number;
  balanceAfter: number;
  confirmedAt: string;
  saleId?: string | null;
  reference?: string | null;
  branchName?: string | null;
  cashier?: string | null;
  notes?: string | null;
}

const typeLabel: Record<string, string> = {
  CreditSale: "دين",
  Repayment: "تسديد",
  DebtReversal: "عكس دين",
  RepaymentReversal: "عكس تسديد",
  Adjustment: "تصحيح",
};

export function PosCollectionScreen({ session, initialCustomerId, initialNumber, initialName, cashSessionId, requireShift = true, picked = null, pickStamp = 0, onSearch, onBack }: {
  session: DeviceSession;
  initialCustomerId: string | null;
  initialNumber: string | null;
  initialName: string | null;
  cashSessionId: string | null;
  requireShift?: boolean;
  picked?: CustomerListItem | null;
  pickStamp?: number;
  onSearch?: () => void;
  onBack: () => void;
}) {
  const customers = createCustomerClient(session);
  const [desktopKeyboard, setDesktopKeyboard] = useState(usesDesktopCustomerKeyboard);
  const [customerId, setCustomerId] = useState(initialCustomerId);
  const [number, setNumber] = useState(initialNumber ?? "");
  const [name, setName] = useState(initialName ?? "");
  const [policy, setPolicy] = useState<CreditPolicy | null>(null);
  const [amount, setAmount] = useState("");
  const amountRef = useRef<HTMLInputElement>(null);
  useEffect(() => { amountRef.current?.focus(); }, []);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [notice, setNotice] = useState("");
  const [successOpen, setSuccessOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [matches, setMatches] = useState<CustomerListItem[]>([]);
  const [operationId, setOperationId] = useState(() => crypto.randomUUID());
  const [movements, setMovements] = useState<Movement[]>([]);
  const [summary, setSummary] = useState({ debt: 0, repaid: 0, balance: 0, stale: false });
  const [movementDocument, setMovementDocument] = useState<DebtDocument | null>(null);

  useEffect(() => {
    setDesktopKeyboard(usesDesktopCustomerKeyboard());
  }, []);

  useEffect(() => {
    if (pickStamp || !picked) return;
    setCustomerId(picked.customerId);
    setNumber(picked.customerNumber ?? "");
    setName(picked.name);
    setMessage("");
  }, [picked, pickStamp]);

  useEffect(() => {
    if (!pickStamp) return;
    if (!picked) {
      setCustomerId(null);
      setNumber("");
      setName("");
      setMessage("");
      return;
    }
    setCustomerId(picked.customerId);
    setNumber(picked.customerNumber ?? "");
    setName(picked.name);
    setMessage("");
  }, [pickStamp, picked]);

  useEffect(() => {
    if (!customerId) {
      setPolicy(null);
      setMovements([]);
      setSummary({ debt: 0, repaid: 0, balance: 0, stale: false });
      return;
    }
    void customers.creditPolicy(customerId).then(setPolicy).catch(() => setPolicy(null));
    void loadHistory(customerId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [customerId]);

  async function loadHistory(id: string) {
    const response = await fetch(`/api/pos/credit-history?customerId=${id}&skip=0&take=20`, { headers: deviceHeaders(session) });
    if (!response.ok) return;
    const payload = await response.json() as { movements?: Movement[]; debtTotal?: number; repaymentTotal?: number; confirmedOutstanding?: number; stale?: boolean };
    setMovements(payload.movements ?? []);
    setSummary({ debt: payload.debtTotal ?? 0, repaid: payload.repaymentTotal ?? 0, balance: payload.confirmedOutstanding ?? 0, stale: Boolean(payload.stale) });
  }

  function choose(customer: CustomerListItem) {
    setCustomerId(customer.customerId);
    setNumber(customer.customerNumber ?? "");
    setName(customer.name);
    setSearchOpen(false);
    setMessage("");
  }

  async function lookup() {
    const term = number.trim();
    if (!term) return;
    const page = await customers.search(term).catch(() => null);
    const exact = page?.customers.find((row) => row.customerNumber === term) ?? page?.customers[0];
    if (!exact) {
      setCustomerId(null);
      setName("");
      setMessage("الزبون غير موجود على هذا الفرع.");
      return;
    }
    choose(exact);
  }

  async function search() {
    const page = await customers.search(number.trim()).catch(() => null);
    setMatches(page?.customers ?? []);
    setSearchOpen(true);
  }

  async function confirm() {
    const value = Number(amount);
    if (busy || !customerId || (requireShift && !cashSessionId) || !(value > 0)) {
      setMessage(requireShift && !cashSessionId ? "افتح الوردية قبل التسديد." : !customerId ? "حدد الزبون." : "أدخل مبلغ التسديد.");
      return;
    }
    setBusy(true);
    try {
      const quote = await fetch("/api/pos/credit-collections/quote", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ customerId, amount: value, receivableId: null }),
      });
      const quoted = await quote.json() as { error?: string | null };
      if (!quote.ok || quoted.error) {
        const text = quoted.error === "The payment exceeds the collectible balance."
          ? "المبلغ المدفوع يتجاوز الرصيد القابل للتحصيل."
          : quoted.error === "The invoice is not collectible."
            ? "الفاتورة غير قابلة للتحصيل."
            : quoted.error ?? "تعذر تسديد الدين.";
        setNotice(text);
        setMessage("");
        return;
      }
      const response = await fetch("/api/pos/credit-collections", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ operationId, customerId, amount: value, receivableId: null, paymentMethod: "Cash", cashSessionId: requireShift ? cashSessionId : null, allowWithoutSession: !requireShift }),
      });
      const payload = await response.json() as { state?: string; message?: string };
      if (payload.state === "Completed" || payload.state === "PendingCentral") {
        setSuccessOpen(true);
        setMessage("");
        setAmount("");
        setOperationId(crypto.randomUUID());
        void customers.creditPolicy(customerId).then(setPolicy).catch(() => undefined);
        return;
      }
      setMessage(payload.message ?? "تم رفض التسديد.");
    } catch {
      setMessage("تعذر تسديد الدين.");
    } finally {
      setBusy(false);
    }
  }

  function press(key: string) {
    if (key === "Enter") {
      const current = window.document.activeElement;
      if (current instanceof HTMLInputElement && current.getAttribute("aria-label") === "رقم الزبون") {
        void lookup();
      }
      const root = current instanceof HTMLElement ? current.closest(".debt-screen") : null;
      const fields = [...(root?.querySelectorAll<HTMLElement>("input, select") ?? [])];
      fields[fields.indexOf(current as HTMLElement) + 1]?.focus();
      return;
    }
    if (key === "/" || key === "*" || key === "-" || key === "+") return;
    if (key === "Del") {
      setAmount("");
      return;
    }
    if (key === "BS") {
      setAmount((current) => current.slice(0, -1));
      return;
    }
    setAmount((current) => key === "." && current.includes(".") ? current : `${current}${key}`);
  }

  async function openDetails(row: Movement) {
    const query = row.saleId ? `saleId=${row.saleId}` : `operationId=${encodeURIComponent(row.operationId)}`;
    const response = await fetch(`/api/pos/credit-documents?${query}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تفاصيل الحركة غير متاحة.");
      return;
    }
    setMovementDocument(await response.json() as DebtDocument);
  }

  if (movementDocument) return <OrderDetailsScreen title="تفاصيل الحركة" document={movementDocument} onClose={() => setMovementDocument(null)} />;

  return (
    <section className="new-customer debt-screen" dir="rtl" aria-label="تسديد دين">
      <ScreenHeader title="تسديد دين" onBack={onBack} />
      <div className="pos-desktop-shell debt-layout">
        <div className="pos-main-workspace debt-main">
          <div className="debt-facts">
            <label className="debt-fact">
              <span>رقم الزبون</span>
              <input aria-label="رقم الزبون" value={number} onChange={(event) => { setNumber(event.target.value); setCustomerId(null); setName(""); }} onKeyDown={(event) => {
                if (event.key !== "Enter") return;
                event.preventDefault();
                event.stopPropagation();
                void lookup();
                const fields = [...event.currentTarget.closest(".debt-screen")!.querySelectorAll<HTMLElement>("input, select")];
                fields[fields.indexOf(event.currentTarget) + 1]?.focus();
              }} />
            </label>
            <div className="debt-fact is-name"><span>اسم الزبون</span><strong>{name || "—"}</strong></div>
            <button type="button" className="ghost-button" onClick={() => onSearch ? onSearch() : void search()}>بحث</button>
          </div>
          <div className="debt-controls">
            <div className="debt-amount"><span>مبلغ التسديد</span><input ref={amountRef} aria-label="مبلغ التسديد" inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value.replace(/[^\d.]/g, ""))} /></div>
            <button type="button" className="debt-register" disabled={busy} onClick={() => void confirm()}>تسديد</button>
            <div className="debt-metric"><span>الحد الائتماني</span><strong>{money(policy?.creditLimit)}</strong></div>
            <div className="debt-metric is-available"><span>المتبقي المتاح</span><strong>{money(policy?.available)}</strong></div>
            <div className="debt-metric is-debt"><span>مجموع الديون</span><strong>{money(policy?.outstanding)}</strong></div>
          </div>
          {message ? <p className="status-error">{message}</p> : null}
          <div className="debt-table-card">
            <h3>سجل الديون والتسديدات للزبون</h3>
            <table>
              <thead>
                <tr><th>#</th><th>التاريخ والوقت</th><th>نوع الحركة</th><th>رقم الطلبية</th><th>المبلغ</th><th>الرصيد بعد الحركة</th><th>الفرع</th><th>الموظف</th><th>ملاحظات</th><th>تفاصيل</th></tr>
              </thead>
              <tbody>
                {movements.map((row, index) => (
                  <tr key={row.operationId}>
                    <td>{index + 1}</td>
                    <td>{new Date(row.confirmedAt).toLocaleString("en-GB")}</td>
                    <td>{typeLabel[row.eventType] ?? row.eventType}</td>
                    <td>{row.reference || "—"}</td>
                    <td>{money(row.amount)}</td>
                    <td>{money(row.balanceAfter)}</td>
                    <td>{row.branchName || "—"}</td>
                    <td>{row.cashier || "—"}</td>
                    <td>{row.notes || "—"}</td>
                    <td><button type="button" onClick={() => void openDetails(row)}>تفاصيل</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="debt-footer">
            {summary.stale ? <p>الرصيد المؤكد من الذمم. سجل الحركات لا يغطي هذا الرصيد لأن حركاته غير موجودة.</p> : null}
            <strong className="is-debt">مجموع الديون {money(summary.debt)}</strong>
            <strong className="is-pay">مجموع التسديدات {money(summary.repaid)}</strong>
            <strong>الرصيد الحالي {money(summary.balance)}</strong>
          </div>
        </div>
        {desktopKeyboard ? (
          <aside className="pos-sidebar pos-pro" aria-label="لوحة الأرقام">
            <section className="pos-side-card">
              <h3>إجراءات سريعة</h3>
              <div className="pos-quick-grid" />
            </section>
            <section className="pos-side-card debt-keypad-card">
              <h3>لوحة الأرقام</h3>
              <PosNumberPad onKey={press} />
            </section>
          </aside>
        ) : null}
      </div>
      <button type="button" className="debt-close" onClick={onBack}>إغلاق</button>
      {notice ? (
        <div className="pos-overlay" role="dialog" aria-label="رسالة التسديد">
          <div className="pos-modal">
            <h3>{notice}</h3>
            <button className="primary-button" type="button" onClick={() => setNotice("")}>إغلاق</button>
          </div>
        </div>
      ) : null}
      {successOpen ? (
        <div className="pos-overlay" role="dialog" aria-label="نجاح التسديد">
          <div className="pos-modal">
            <h3>تم تسديد الدين بنجاح</h3>
            <button className="primary-button" type="button" onClick={() => setSuccessOpen(false)}>موافق</button>
          </div>
        </div>
      ) : null}
      {searchOpen ? (
        <div className="pos-overlay" role="dialog" aria-label="بحث عن زبون">
          <div className="pos-modal">
            <h3>بحث عن زبون</h3>
            {matches.length === 0 ? <p>لا توجد نتائج.</p> : matches.map((row) => (
              <button key={row.customerId} type="button" onClick={() => choose(row)}>{row.customerNumber || "—"} · {row.name}</button>
            ))}
            <button className="ghost-button" type="button" onClick={() => setSearchOpen(false)}>إغلاق</button>
          </div>
        </div>
      ) : null}
    </section>
  );
}

function money(value: number | null | undefined) {
  return value == null || Number.isNaN(value) ? "—" : value.toLocaleString("en-GB", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
