import { useEffect, useState } from "react";
import { ArrowLeft } from "lucide-react";
import { SetupClientError, deviceHeaders } from "../api/client";
import type { CreditPolicy } from "../api/customers";
import { createCustomerClient } from "../api/customers";
import type { CartDraft } from "../cart/model";
import { cartTotals } from "../cart/model";
import { creditEligible, deferredAmount } from "../credit/creditSale";
import { FullKeyboard } from "./FullKeyboard";
import { ScreenHeader } from "./ScreenHeader";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";

export function CreditSaleScreen({ session, cart, customerName, branchName, userName, canCreate, operationId, onBack, onCompleted }: {
  session: DeviceSession;
  cart: CartDraft;
  customerName: string;
  branchName: string;
  userName: string;
  canCreate: boolean;
  operationId: string;
  onBack: () => void;
  onCompleted: () => void;
}) {
  const customers = createCustomerClient(session);
  const [desktopKeyboard, setDesktopKeyboard] = useState(usesDesktopCustomerKeyboard);
  const [policy, setPolicy] = useState<CreditPolicy | null>(null);
  const [immediate, setImmediate] = useState("0");
  const [state, setState] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const total = Number(cartTotals(cart).total);
  const split = deferredAmount({ invoiceTotal: total, immediatePayment: Number(immediate || "0") });
  const deferred = "deferred" in split ? split.deferred : null;
  const block = creditEligible(policy, cart.customerId, canCreate);
  const projected = policy && deferred !== null ? policy.outstanding + deferred : null;

  useEffect(() => {
    setDesktopKeyboard(usesDesktopCustomerKeyboard());
    if (!cart.customerId) return;
    void customers.creditPolicy(cart.customerId).then(setPolicy).catch((error: unknown) => {
      setPolicy(null);
      setMessage(error instanceof SetupClientError ? error.message : "جهة الائتمان المركزية غير متاحة.");
    });
    // The client identity changes every render; the customer id is the load key.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cart.customerId]);

  useEffect(() => {
    if (desktopKeyboard || !window.visualViewport) return undefined;
    const viewport = window.visualViewport;
    const apply = () => {
      const inset = Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop);
      document.documentElement.style.setProperty("--keyboard-inset", `${inset}px`);
    };
    viewport.addEventListener("resize", apply);
    return () => {
      viewport.removeEventListener("resize", apply);
      document.documentElement.style.removeProperty("--keyboard-inset");
    };
  }, [desktopKeyboard]);

  async function confirm() {
    if (busy || block || deferred === null || !cart.customerId) {
      setMessage(block ?? ("error" in split ? split.error : ""));
      return;
    }
    if (deferred > policy!.available) {
      setMessage("المبلغ المؤجل يتجاوز المتاح للاستدانة.");
      return;
    }
    setBusy(true);
    try {
      const response = await fetch("/api/pos/credit-sales", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({
          operationId,
          cartId: cart.cartId,
          customerId: cart.customerId,
          invoiceTotal: total,
          immediatePayment: Number(immediate || "0"),
          lines: cart.lines.map((line) => line.lineType === "Fixed"
            ? { itemId: line.itemId, quantity: Number(line.quantity), lineType: "Fixed", expectedUnitPrice: Number(line.resolvedUnitPrice), priceOverrideMode: line.priceOverrideMode, manualUnitPrice: line.manualUnitPrice ? Number(line.manualUnitPrice) : null, manualLineTotal: line.manualLineTotal ? Number(line.manualLineTotal) : null }
            : { itemId: line.referenceItemId, quantity: Number(line.quantity), lineType: "VariableValueByWeight", enteredScaleValue: Number(line.enteredScaleValue), expectedFinalSellingPrice: Number(line.finalSellingPrice), expectedReferenceBasePrice: Number(line.referenceBasePrice), expectedWeightKg: Number(line.weightKg), expectedStockOwnerQuantity: Number(line.projectedStockOwnerQuantity), priceOverrideMode: line.priceOverrideMode, manualUnitPrice: line.manualUnitPrice ? Number(line.manualUnitPrice) : null, manualLineTotal: line.manualLineTotal ? Number(line.manualLineTotal) : null }),
        }),
      });
      const payload = await response.json() as { state?: string; message?: string };
      setState(payload.state ?? "");
      if (payload.state === "Completed") {
        setMessage("تم تأكيد البيع بالدين.");
        onCompleted();
        return;
      }
      if (payload.state === "PendingCentral") setMessage("العملية قيد التأكيد.");
      else if (payload.state === "ReconciliationRequired") setMessage("العملية بحاجة إلى مراجعة مالية.");
      else setMessage(payload.message?.toLowerCase().includes("price") ? "تغيرت الأسعار. حدّث الفاتورة ثم أكّد المبلغ الجديد." : (payload.message ?? "تم رفض البيع بالدين. السلة ما زالت موجودة."));
    } catch {
      setMessage("العملية قيد التأكيد.");
      setState("PendingCentral");
    } finally {
      setBusy(false);
    }
  }

  async function refresh() {
    const response = await fetch(`/api/pos/credit-sales/${operationId}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تعذر تحديث حالة العملية.");
      return;
    }
    const payload = await response.json() as { centrallyConfirmed?: boolean; reconciliationRequired?: boolean };
    if (payload.centrallyConfirmed) {
      setState("Completed");
      setMessage("تم تأكيد البيع بالدين.");
      onCompleted();
    } else if (payload.reconciliationRequired) {
      setState("ReconciliationRequired");
      setMessage("العملية بحاجة إلى مراجعة مالية.");
    } else setMessage("العملية قيد التأكيد.");
  }

  const due = new Date();
  due.setDate(due.getDate() + (policy?.paymentTermDays ?? 30));

  return (
    <section className={desktopKeyboard ? "new-customer full-keyboard-screen has-keyboard" : "new-customer"} dir="rtl" aria-label="البيع بالدين / تأجيل الدفع">
      <ScreenHeader title="البيع بالدين / تأجيل الدفع" onBack={onBack} />
      <header className="new-customer-bar shell-page-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <p>{branchName} · {userName}</p>
        <h2>البيع بالدين / تأجيل الدفع</h2>
        <button type="button" className="new-customer-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      <div className="new-customer-card credit-sale-sheet">
        <p>الزبون: {customerName}</p>
        <p>حالة الائتمان: {policy?.creditStatus ?? "—"} · الحد الائتماني {policy?.creditLimit ?? "—"} · الدين السابق {policy?.outstanding ?? "—"} · المبالغ المحجوزة {policy ? Math.max(0, policy.creditLimit - policy.outstanding - policy.available) : "—"} · المتاح للاستدانة {policy?.available ?? "—"}</p>
        <p>إجمالي الفاتورة {total} NIS</p>
        <label><span>المدفوع الآن</span><input aria-label="المدفوع الآن" inputMode="decimal" value={immediate} onChange={(event) => setImmediate(event.target.value.replace(/[^\d.]/g, ""))} onFocus={(event) => event.currentTarget.scrollIntoView({ block: "nearest" })} /></label>
        <p>المبلغ المؤجل {deferred ?? "—"} NIS</p>
        <p>الرصيد المتوقع بعد العملية {projected ?? "—"} NIS</p>
        <p>تاريخ الاستحقاق {due.toLocaleDateString("en-GB")}</p>
        {block ? <p className="status-error">{block}</p> : null}
        {message ? <p>{message}</p> : null}
        <p>مرجع العملية {operationId}</p>
        <button type="button" disabled={busy || Boolean(block) || deferred === null || state === "Completed" || state === "PendingCentral" || state === "ReconciliationRequired"} onClick={() => void confirm()}>تأكيد البيع بالدين</button>
        {state === "PendingCentral" ? <button type="button" onClick={() => void refresh()}>تحديث الحالة</button> : null}
      </div>
      {desktopKeyboard ? <FullKeyboard onEnter={() => undefined} /> : null}
    </section>
  );
}
