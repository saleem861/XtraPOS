import { useEffect, useState } from "react";
import { ArrowLeft } from "lucide-react";
import { deviceHeaders } from "../api/client";
import { invoiceLabel, previewAllocation, type AllocationLine, type DebtInvoice } from "../credit/debtAccount";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { DebtReversalScreen } from "./DebtReversalScreen";
import { NumericEditModal } from "./NumericEditModal";
import type { DeviceSession } from "../types/setup";
import type { DebtCustomer } from "./DebtAccountScreen";

function operationKey(customerId: string): string {
  return `xtrapos-collection-${customerId}`;
}

export function DebtRepaymentScreen({ session, customer, branchName, cashierName, cashSessionLabel, canReverse, onBack }: {
  session: DeviceSession;
  customer: DebtCustomer;
  branchName: string;
  cashierName: string;
  cashSessionLabel: string;
  canReverse: boolean;
  onBack: () => void;
}) {
  const [rows, setRows] = useState<DebtInvoice[]>([]);
  const [amount, setAmount] = useState("");
  const [selected, setSelected] = useState("");
  const [quoteLines, setQuoteLines] = useState<AllocationLine[] | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const [received, setReceived] = useState(false);
  const [busy, setBusy] = useState(false);
  const [state, setState] = useState("");
  const [message, setMessage] = useState("");
  const [operationId, setOperationId] = useState("");
  const [keypad, setKeypad] = useState(false);
  const [outstanding, setOutstanding] = useState<number | null>(null);
  const [balanceAtCollection, setBalanceAtCollection] = useState<number | null>(null);
  const [collectedAt, setCollectedAt] = useState<string | null>(null);
  const [reversing, setReversing] = useState(false);
  const [reversalState, setReversalState] = useState("");
  useEffect(() => {
    if (usesDesktopCustomerKeyboard() || !window.visualViewport) return undefined;
    const viewport = window.visualViewport;
    const apply = () => document.documentElement.style.setProperty("--keyboard-inset", `${Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop)}px`);
    viewport.addEventListener("resize", apply);
    return () => { viewport.removeEventListener("resize", apply); document.documentElement.style.removeProperty("--keyboard-inset"); };
  }, []);
  useEffect(() => {
    void (async () => {
      const stored = sessionStorage.getItem(operationKey(customer.customerId));
      if (stored) {
        setOperationId(stored);
        const status = await fetch(`/api/pos/credit-collections/${stored}`, { headers: deviceHeaders(session) }).catch(() => null);
        if (status?.ok) {
          const body = await status.json() as { state?: string; amount?: number; collectedAt?: string };
          if (body.state === "Completed") {
            setState("Completed");
            setAmount(String(body.amount ?? ""));
            setCollectedAt(body.collectedAt ?? null);
            const summary = await fetch(`/api/pos/credit-collections/customers/${customer.customerId}/summary`, { headers: deviceHeaders(session) }).catch(() => null);
            if (summary?.ok) setOutstanding((await summary.json()).outstanding);
          }
        }
      }
      else {
        const listed = await fetch(`/api/pos/credit-collections/customers/${customer.customerId}/operations`, { headers: deviceHeaders(session) }).catch(() => null);
        const open = listed?.ok ? ((await listed.json()).operations as { operationId: string; state: string }[]).find((row) => row.state !== "Completed") : undefined;
        const next = open?.operationId ?? crypto.randomUUID();
        if (open) setState(open.state === "ReconciliationRequired" ? "ReconciliationRequired" : "PendingCentral");
        sessionStorage.setItem(operationKey(customer.customerId), next);
        setOperationId(next);
      }
    })();
    void fetch(`/api/pos/credit-collections/customers/${customer.customerId}/receivables`, { headers: deviceHeaders(session) })
      .then(async (response) => {
        if (!response.ok) throw new Error("unavailable");
        const body = await response.json() as { receivables: DebtInvoice[] };
        setRows(body.receivables.map((row) => ({ ...row, dueDate: String(row.dueDate) })));
      })
      .catch(() => setMessage("جهة الائتمان المركزية غير متاحة."));
  }, [customer.customerId, session]);
  useEffect(() => {
    if (state !== "Completed" || !operationId || !canReverse) return;
    void fetch(`/api/pos/credit-reversals/collections/${operationId}`, { headers: deviceHeaders(session) })
      .then(async (response) => response.ok ? response.json() as Promise<{ state?: string; reversalOperationId?: string }> : null)
      .then((body) => { if (body?.state && body.state !== "Eligible") setReversalState(`${body.state}|${body.reversalOperationId ?? ""}`); })
      .catch(() => undefined);
  }, [state, operationId, canReverse, session, reversing]);
  const preview = previewAllocation(rows, Number(amount || "0"), selected || null);
  if (reversing && operationId) {
    return <DebtReversalScreen session={session} customer={customer} collectionId={operationId} branchName={branchName} cashSessionLabel={cashSessionLabel} onBack={() => setReversing(false)} />;
  }
  async function quote() {
    const response = await fetch("/api/pos/credit-collections/quote", {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({ customerId: customer.customerId, amount: Number(amount), receivableId: selected || null }),
    });
    if (!response.ok) {
      setMessage("تعذر تحديث عرض التوزيع.");
      setQuoteLines(null);
      setAcknowledged(false);
      return;
    }
    const body = await response.json() as { allocations: AllocationLine[]; error?: string | null; outstanding: number };
    setOutstanding(body.outstanding);
    if (body.error) {
      setMessage(body.error);
      setQuoteLines(null);
      setAcknowledged(false);
      return;
    }
    const same = JSON.stringify(body.allocations) === JSON.stringify(preview.lines);
    setQuoteLines(body.allocations);
    setAcknowledged(same);
    setMessage(same ? "راجع التوزيع ثم أكّد استلام النقد." : "تغير الرصيد. راجع التوزيع الجديد قبل التأكيد.");
  }
  async function confirm() {
    if (busy || !received || !acknowledged || !quoteLines || preview.error) return;
    setBusy(true);
    try {
      const response = await fetch("/api/pos/credit-collections", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ operationId, customerId: customer.customerId, amount: Number(amount), receivableId: selected || null, paymentMethod: "Cash" }),
      });
      const payload = await response.json() as { state?: string };
      setState(payload.state ?? "");
      if (payload.state === "Completed") {
        setBalanceAtCollection(preview.remaining);
        sessionStorage.removeItem(operationKey(customer.customerId));
        setMessage("تم تسديد الدين.");
        const summary = await fetch(`/api/pos/credit-collections/customers/${customer.customerId}/summary`, { headers: deviceHeaders(session) });
        if (summary.ok) setOutstanding((await summary.json()).outstanding);
        const status = await fetch(`/api/pos/credit-collections/${operationId}`, { headers: deviceHeaders(session) });
        if (status.ok) setCollectedAt((await status.json()).collectedAt ?? null);
      } else if (payload.state === "PendingCentral") setMessage("تم تسجيل التحصيل، والعملية قيد التأكيد المالي");
      else if (payload.state === "ReconciliationRequired") setMessage("عملية التسديد بحاجة إلى مراجعة مالية");
      else setMessage("تم رفض التسديد قبل تسجيل النقد.");
    } catch {
      setState("PendingCentral");
      setMessage("تم تسجيل التحصيل، والعملية قيد التأكيد المالي");
    } finally {
      setBusy(false);
    }
  }
  async function refresh() {
    const response = await fetch(`/api/pos/credit-collections/${operationId}`, { headers: deviceHeaders(session) });
    if (!response.ok) return;
    const payload = await response.json() as { centrallyConfirmed?: boolean; reconciliationRequired?: boolean };
    if (payload.centrallyConfirmed) {
      setState("Completed");
      setMessage("تم تسديد الدين.");
      sessionStorage.removeItem(operationKey(customer.customerId));
    } else if (payload.reconciliationRequired) {
      setState("ReconciliationRequired");
      setMessage("عملية التسديد بحاجة إلى مراجعة مالية");
    }
  }
  const locked = state === "PendingCentral" || state === "ReconciliationRequired" || state === "Completed";
  return (
    <section className="new-customer" dir="rtl" aria-label="تسديد دين">
      <header className="new-customer-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <h2>تسديد دين</h2>
        <button type="button" className="new-customer-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      <div className="new-customer-card credit-sale-sheet">
        <p>{customer.name} · {customer.customerNumber ?? "—"}</p>
        <p>فرع التحصيل {branchName} · الكاشير {cashierName} · الجهاز {session.deviceName} · الجلسة {cashSessionLabel}</p>
        <p>{state === "Completed" ? "الدين القائم الآن" : "الدين القائم"} {outstanding ?? "—"} NIS · طريقة الدفع: نقدي</p>
        <label>المبلغ<button type="button" onClick={() => setKeypad(true)}>{amount || "0"}</button></label>
        <label>فاتورة محددة<select aria-label="فاتورة محددة" value={selected} onChange={(event) => { setSelected(event.target.value); setAcknowledged(false); }} disabled={locked}><option value="">توزيع تلقائي</option>{rows.map((row) => <option key={row.receivableId} value={row.receivableId}>{invoiceLabel(row)} · {row.outstandingAmount}</option>)}</select></label>
        <button type="button" disabled={locked || Boolean(preview.error)} onClick={() => void quote()}>تحديث التوزيع</button>
        {preview.error ? <p className="status-error">{preview.error}</p> : null}
        <ul>{(quoteLines ?? []).map((line) => <li key={line.receivableId}>{line.receivableId.slice(0, 8)}: {line.amount} NIS</li>)}</ul>
        <p>المتبقي بعد الدفع {preview.error ? "—" : preview.remaining} NIS</p>
        <label><input type="checkbox" checked={received} onChange={(event) => setReceived(event.target.checked)} disabled={locked} />تم استلام النقد فعلياً</label>
        {message ? <p>{message}</p> : null}
        <p>مرجع العملية {operationId}</p>
        <button type="button" disabled={busy || locked || !received || !acknowledged} onClick={() => void confirm()}>تأكيد استلام وتسديد الدين</button>
        {state === "PendingCentral" || state === "ReconciliationRequired" ? <button type="button" onClick={() => void refresh()}>تحديث الحالة</button> : null}
        {state === "Completed" ? (
          <article aria-label="إيصال تسديد دين">
            <h3>XtraPOS · إيصال تسديد دين</h3>
            <p>المرجع {operationId}</p>
            <p>{customer.name} · {customer.customerNumber ?? "—"}</p>
            <p>فرع التحصيل {branchName} · {cashierName}</p>
            <p>نقدي · {amount} NIS · {collectedAt ?? ""} · الحالة مكتمل</p>
            <ul>{(quoteLines ?? []).map((line) => {
              const invoice = rows.find((row) => row.receivableId === line.receivableId);
              return <li key={line.receivableId}>{invoiceLabel({ receivableId: line.receivableId, sourceSaleNumber: invoice?.sourceSaleNumber ?? null })} · {invoice?.sourceBranchName ?? "فرع المصدر غير معروف"} · {line.amount} NIS</li>;
            })}</ul>
            <p>الرصيد عند التسديد {balanceAtCollection ?? "—"} NIS</p>
            <p>الدين القائم الآن {outstanding ?? "—"} NIS</p>
            {reversalState.startsWith("Completed") ? <p>تم عكس التسديد · {reversalState.split("|")[1]}</p> : null}
            <button type="button" onClick={() => window.print()}>طباعة</button>
            {canReverse && !reversalState.startsWith("Completed") ? <button type="button" onClick={() => setReversing(true)}>عكس تسديد الدين</button> : null}
          </article>
        ) : null}
      </div>
      {keypad ? <NumericEditModal title="مبلغ التسديد" ariaLabel="مبلغ التسديد" inputLabel="المبلغ" initialValue={amount} allowDecimal error="" onCancel={() => setKeypad(false)} onConfirm={(value) => { setAmount(value); setAcknowledged(false); setKeypad(false); }} /> : null}
    </section>
  );
}
