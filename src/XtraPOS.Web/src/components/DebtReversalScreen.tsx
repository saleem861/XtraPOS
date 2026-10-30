import { useEffect, useState } from "react";
import { ArrowLeft } from "lucide-react";
import { deviceHeaders } from "../api/client";
import { shownReversalDebt } from "../credit/reversalPreview";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { FullKeyboard } from "./FullKeyboard";
import type { DeviceSession } from "../types/setup";

type Line = { amount: number; sourceSaleNumber: number | null; sourceBranchName: string | null };
type Preview = {
  amount: number;
  collectedAt: string;
  cashierName: string;
  state: string;
  reversalOperationId: string | null;
  cashEffect: string | null;
  outstanding: number;
  expectedOutstanding: number;
  allocations: Line[];
};

function operationKey(collectionId: string) {
  return `xtrapos-reversal-${collectionId}`;
}

export function DebtReversalScreen({ session, customer, collectionId, branchName, cashSessionLabel, onBack }: {
  session: DeviceSession;
  customer: { customerId: string; customerNumber?: string | null; name: string };
  collectionId: string;
  branchName: string;
  cashSessionLabel: string;
  onBack: () => void;
}) {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [reason, setReason] = useState<"" | "RefundToCustomer" | "IncorrectRecording">("");
  const [notes, setNotes] = useState("");
  const [cashBack, setCashBack] = useState(false);
  const [ack, setAck] = useState(false);
  const [operationId, setOperationId] = useState("");
  const [state, setState] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [desktopKeyboard, setDesktopKeyboard] = useState(usesDesktopCustomerKeyboard);
  const [editing, setEditing] = useState(false);
  const headers = deviceHeaders(session);

  async function load() {
    const response = await fetch(`/api/pos/credit-reversals/collections/${collectionId}`, { headers });
    if (!response.ok) {
      setMessage("تعذر قراءة أهلية العكس.");
      setPreview(null);
      return;
    }
    const body = await response.json() as Preview;
    setPreview(body);
    setAck(false);
    if (body.state === "Completed") setState("Completed");
    if (body.state === "Pending" || body.state === "ReconciliationRequired") setState(body.state);
    if (body.reversalOperationId) setOperationId(body.reversalOperationId);
  }

  useEffect(() => {
    const stored = sessionStorage.getItem(operationKey(collectionId)) ?? crypto.randomUUID();
    sessionStorage.setItem(operationKey(collectionId), stored);
    setOperationId(stored);
    void load();
    const media = window.matchMedia("(pointer: fine) and (hover: hover)");
    const syncKeyboard = () => setDesktopKeyboard(media.matches);
    syncKeyboard();
    media.addEventListener("change", syncKeyboard);
    if (media.matches || !window.visualViewport) return () => media.removeEventListener("change", syncKeyboard);
    const viewport = window.visualViewport;
    const apply = () => document.documentElement.style.setProperty("--keyboard-inset", `${Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop)}px`);
    viewport.addEventListener("resize", apply);
    return () => { media.removeEventListener("change", syncKeyboard); viewport.removeEventListener("resize", apply); document.documentElement.style.removeProperty("--keyboard-inset"); };
  }, [collectionId]);

  async function confirm() {
    if (!preview || busy || !reason || !notes.trim() || !ack || preview.state !== "Eligible") return;
    setBusy(true);
    try {
      const response = await fetch("/api/pos/credit-reversals", {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ operationId, collectionOperationId: collectionId, customerId: customer.customerId, reasonCode: reason, notes: notes.trim() }),
      });
      const payload = await response.json() as { state?: string };
      setState(payload.state ?? "");
      if (payload.state === "Completed") {
        setMessage("تم عكس التسديد بنجاح");
        await load();
      } else if (payload.state === "PendingCentral") setMessage("عملية العكس قيد التأكيد المالي");
      else if (payload.state === "ReconciliationRequired") setMessage("عملية العكس بحاجة إلى مراجعة مالية");
      else setMessage("تم رفض العكس. راجع حالة العملية قبل إعادة المحاولة.");
    } catch {
      setState("PendingCentral");
      setMessage("عملية العكس قيد التأكيد المالي");
    } finally {
      setBusy(false);
    }
  }

  async function refresh() {
    const response = await fetch(`/api/pos/credit-reversals/${operationId}`, { headers });
    if (!response.ok) return;
    const body = await response.json() as { state: string };
    setState(body.state);
    if (body.state === "Completed") {
      setMessage("تم عكس التسديد بنجاح");
      await load();
    } else if (body.state === "ReconciliationRequired") setMessage("عملية العكس بحاجة إلى مراجعة مالية");
    else setMessage("عملية العكس قيد التأكيد المالي");
  }

  const effect = reason === "RefundToCustomer" ? "إرجاع نقدي من الجلسة الحالية" : reason === "IncorrectRecording" ? "تصحيح دفتري بلا إرجاع نقدي" : "—";
  const locked = state === "Completed" || state === "PendingCentral" || state === "ReconciliationRequired" || preview?.state === "Completed";
  const posted = state === "Completed" || preview?.state === "Completed";
  const debt = preview ? shownReversalDebt({ outstanding: preview.outstanding, expectedOutstanding: preview.expectedOutstanding, state: posted ? "Completed" : preview.state, reason: reason === "IncorrectRecording" ? "IncorrectRecording" : "RefundToCustomer" }) : null;
  const showKeyboard = desktopKeyboard && editing && !locked;

  return (
    <section className={showKeyboard ? "new-customer full-keyboard-screen has-keyboard" : "new-customer"} dir="rtl" aria-label="عكس عملية تسديد دين">
      <header className="new-customer-bar"><strong className="new-customer-brand">XtraPOS</strong><h2>عكس عملية تسديد دين</h2><button type="button" className="new-customer-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button></header>
      <div className="new-customer-card">
        <p>{customer.name} · {customer.customerNumber ?? "—"}</p>
        <p>مرجع التسديد {collectionId}</p>
        <p>تاريخ التسديد {preview?.collectedAt ?? "—"} · الكاشير {preview?.cashierName ?? "—"} · {branchName}</p>
        <p>طريقة الدفع: نقدي · المبلغ {preview?.amount ?? "—"} NIS</p>
        <ul>{(preview?.allocations ?? []).map((line, index) => <li key={index}>{line.sourceSaleNumber != null ? `فاتورة ${line.sourceSaleNumber}` : "مرجع الذمة"} · {line.sourceBranchName ?? "فرع المصدر غير معروف"} · {line.amount} NIS</li>)}</ul>
        <p>{debt?.confirmed ? `الدين القائم بعد العكس ${debt.current} NIS` : `الدين القائم ${debt?.current ?? "—"} NIS · بعد العكس ${debt?.after ?? "—"} NIS`}</p>
        <p>الجلسة {cashSessionLabel} · نوع التصحيح {effect}</p>
        <fieldset disabled={locked}>
          <legend>نوع العكس</legend>
          <label><input type="radio" name="reversal-reason" checked={reason === "RefundToCustomer"} onChange={() => { setReason("RefundToCustomer"); setAck(false); }} />إرجاع المبلغ للزبون</label>
          <label><input type="radio" name="reversal-reason" checked={reason === "IncorrectRecording"} onChange={() => { setReason("IncorrectRecording"); setAck(false); }} />تصحيح تسجيل تسديد خاطئ</label>
        </fieldset>
        {reason === "RefundToCustomer" ? <p>سيُعاد كامل مبلغ التسديد إلى الزبون من الجلسة المفتوحة.</p> : null}
        {reason === "IncorrectRecording" ? <p>التسديد سُجّل بالخطأ والنقد لم يُستلم. هذا تصحيح دفتري وليس إرجاعاً نقدياً.</p> : null}
        <label>سبب العكس / ملاحظات
          <textarea value={notes} onChange={(event) => { setNotes(event.target.value); setAck(false); }} onPointerDown={() => { if (!locked) setEditing(true); }} onFocus={() => { if (!locked) setEditing(true); }} readOnly={locked} />
        </label>
        <label><input type="checkbox" checked={cashBack} onChange={(event) => setCashBack(event.target.checked)} disabled={locked || reason !== "RefundToCustomer"} />تم تسليم النقد للزبون</label>
        <label><input type="checkbox" checked={ack} onChange={(event) => setAck(event.target.checked)} disabled={locked || !preview || preview.state !== "Eligible" || !reason || !notes.trim() || (reason === "RefundToCustomer" && !cashBack)} />أؤكد عكس {preview?.amount ?? "—"} NIS ليصبح الدين {debt?.after ?? "—"} NIS</label>
        {message ? <p>{message}</p> : null}
        <p>مرجع العكس {operationId}</p>
        <button type="button" disabled={busy || locked || !ack} onClick={() => void confirm()}>تأكيد عكس التسديد</button>
        {state === "PendingCentral" || state === "ReconciliationRequired" ? <button type="button" onClick={() => void refresh()}>تحديث الحالة</button> : null}
        {state === "Completed" || preview?.state === "Completed" ? (
          <article aria-label="إشعار عكس تسديد دين">
            <h3>XtraPOS · إشعار عكس تسديد دين</h3>
            <p>مرجع التسديد {collectionId}</p>
            <p>مرجع العكس {preview?.reversalOperationId ?? operationId}</p>
            <p>{customer.name} · {customer.customerNumber ?? "—"} · {branchName}</p>
            <p>{reason === "IncorrectRecording" || preview?.cashEffect === "RecordingCorrection" ? "تصحيح تسجيل تسديد خاطئ" : "إرجاع المبلغ للزبون"} · {notes}</p>
            <p>{preview?.amount ?? "—"} NIS · الدين الناتج {debt?.current ?? "—"} NIS · مكتمل</p>
            <button type="button" onClick={() => window.print()}>طباعة</button>
          </article>
        ) : null}
      </div>
      {showKeyboard ? <FullKeyboard onEnter={() => setEditing(false)} /> : null}
    </section>
  );
}
