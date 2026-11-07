import { useEffect, useState } from "react";
import { deviceHeaders } from "../api/client";
import type { CreditPolicy } from "../api/customers";
import { createCustomerClient } from "../api/customers";
import type { CartDraft } from "../cart/model";
import { cartTotals } from "../cart/model";
import { subtractDecimals } from "../cart/decimal";
import { appendDeferred, creditEligible, creditLimitMessage, splitDeferred } from "../credit/creditSale";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";
import { OrderDetailsScreen, type DebtDocument } from "./OrderDetailsScreen";
import { NumericKeypad } from "./NumericKeypad";
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
  pending?: boolean;
}

const typeLabel: Record<string, string> = {
  CreditSale: "دين",
  Repayment: "تسديد",
  DebtReversal: "عكس دين",
  RepaymentReversal: "عكس تسديد",
  Adjustment: "تصحيح",
};

export function CreditSaleScreen({ session, cart, customerName, customerNumber = null, cashSessionId = null, canCreate, operationId, onBack, onPosted }: {
  session: DeviceSession;
  cart: CartDraft;
  customerName: string;
  customerNumber?: string | null;
  cashSessionId?: string | null;
  branchName: string;
  userName: string;
  canCreate: boolean;
  operationId: string;
  onBack: () => void;
  onPosted: () => void;
}) {
  const customers = createCustomerClient(session);
  const [desktopKeyboard, setDesktopKeyboard] = useState(usesDesktopCustomerKeyboard);
  const [policy, setPolicy] = useState<CreditPolicy | null>(null);
  const [deferredInput, setDeferredInput] = useState("");
  const [touched, setTouched] = useState(false);
  const [state, setState] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [movements, setMovements] = useState<Movement[]>([]);
  const [summary, setSummary] = useState({ debt: 0, repaid: 0, balance: 0, stale: false });
  const [skip, setSkip] = useState(0);
  const [totalCount, setTotalCount] = useState(0);
  const [document, setDocument] = useState<DebtDocument | null>(null);
  const [documentTitle, setDocumentTitle] = useState("");
  const total = Number(cartTotals(cart).total);
  const shownDeferred = touched ? deferredInput : (deferredInput || String(total));
  const split = splitDeferred(total, Number(shownDeferred || "0"));
  const deferred = "deferred" in split ? split.deferred : null;
  const immediate = "immediate" in split ? split.immediate : null;
  const block = creditEligible(policy, cart.customerId, canCreate);

  useEffect(() => {
    setDesktopKeyboard(usesDesktopCustomerKeyboard());
    if (!touched) setDeferredInput(String(total));
  }, [total, touched]);

  useEffect(() => {
    if (!cart.customerId) return;
    void customers.creditPolicy(cart.customerId).then(setPolicy).catch(() => {
      setPolicy(null);
      setMessage("جهة الائتمان المركزية غير متاحة.");
    });
    void loadHistory(0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cart.customerId]);

  async function loadHistory(nextSkip: number) {
    if (!cart.customerId) return;
    const response = await fetch(`/api/pos/credit-history?customerId=${cart.customerId}&skip=${nextSkip}&take=20`, { headers: deviceHeaders(session) });
    if (!response.ok) return;
    const payload = await response.json() as { movements?: Movement[]; debtTotal?: number; repaymentTotal?: number; confirmedOutstanding?: number; stale?: boolean; totalCount?: number };
    setMovements((current) => nextSkip === 0 ? (payload.movements ?? []) : [...current, ...(payload.movements ?? [])]);
    setSummary({ debt: payload.debtTotal ?? 0, repaid: payload.repaymentTotal ?? 0, balance: payload.confirmedOutstanding ?? 0, stale: Boolean(payload.stale) });
    setTotalCount(payload.totalCount ?? 0);
    setSkip(nextSkip);
  }

  async function confirm() {
    const limitMessage = policy && deferred !== null ? creditLimitMessage(policy.available, deferred) : null;
    if (busy || block || deferred === null || immediate === null || !cart.customerId || limitMessage) {
      setState(limitMessage ? "Refused" : "");
      setMessage(limitMessage ?? block ?? ("error" in split ? split.error : ""));
      return;
    }
    setBusy(true);
    try {
      const invoiceTotal = cartTotals(cart).total;
      const deferredText = shownDeferred.endsWith(".") ? shownDeferred.slice(0, -1) : shownDeferred;
      const immediateText = subtractDecimals(invoiceTotal, deferredText || "0");
      const response = await fetch("/api/pos/credit-sales", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: `{"operationId":"${operationId}","cartId":"${cart.cartId}","customerId":"${cart.customerId}","invoiceTotal":${invoiceTotal},"immediatePayment":${immediateText},"cashSessionId":${cashSessionId ? `"${cashSessionId}"` : "null"},"lines":[${cart.lines.map(creditLineJson).join(",")}]}`,
      });
      const payload = await response.json() as { state?: string; message?: string };
      setState(payload.state ?? "");
      if (payload.state === "Completed") {
        setState("");
        setTouched(false);
        setMessage("تم تأكيد الدين.");
        await Promise.all([
          loadHistory(0),
          customers.creditPolicy(cart.customerId).then(setPolicy).catch(() => setMessage("تم تأكيد الدين. تعذر تحديث الحد الائتماني.")),
        ]);
        onPosted();
        return;
      }
      if (payload.state === "PendingCentral") setMessage("العملية محفوظة محلياً وقيد التأكيد المركزي. ليست ديناً مؤكداً بعد.");
      else if (payload.state === "ReconciliationRequired") setMessage("العملية بحاجة إلى مراجعة مالية.");
      else setMessage(payload.message ?? "تم رفض البيع بالدين. السلة ما زالت موجودة.");
    } catch {
      setMessage("العملية قيد التأكيد.");
      setState("PendingCentral");
    } finally {
      setBusy(false);
    }
  }

  async function openDetails(row: Movement) {
    const query = row.saleId ? `saleId=${row.saleId}` : `operationId=${encodeURIComponent(row.operationId)}`;
    const response = await fetch(`/api/pos/credit-documents?${query}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تفاصيل الحركة غير متاحة.");
      return;
    }
    setDocument(await response.json() as DebtDocument);
    setDocumentTitle(row.eventType === "CreditSale" || row.eventType === "DebtReversal" ? "تفاصيل الطلبية" : "تفاصيل سند التسديد");
  }

  if (document) {
    return <OrderDetailsScreen title={documentTitle} document={document} onClose={() => setDocument(null)} />;
  }

  const pending = state === "PendingCentral";
  return (
    <section className="new-customer debt-screen" dir="rtl" aria-label="تأجيل دفع للزبون">
      <ScreenHeader title="تأجيل دفع" onBack={onBack} />
      <div className="pos-desktop-shell debt-layout">
        <div className="pos-main-workspace debt-main">
          <div className="debt-facts">
            <div className="debt-fact"><span>رقم الزبون</span><strong>{customerNumber || "—"}</strong></div>
            <div className="debt-fact is-name"><span>اسم الزبون</span><strong>{customerName}</strong></div>
            <div className="debt-fact"><span>رقم الطلبية الفعالة</span><strong>مسودة حالية</strong></div>
            <div className="debt-fact"><span>المجموع بعد الخصم</span><strong>{money(total)}</strong></div>
          </div>
          <div className="debt-controls">
            <div className="debt-amount"><span>القيمة المؤجلة</span><input aria-label="القيمة المؤجلة" inputMode="decimal" value={shownDeferred} onChange={(event) => { setTouched(true); setDeferredInput(event.target.value.replace(/[^\d.]/g, "")); }} /></div>
            <button type="button" className="debt-register" disabled={busy || Boolean(block) || deferred === null || pending || state === "ReconciliationRequired"} onClick={() => void confirm()}>تسجيل الدين</button>
            <div className="debt-metric"><span>الحد الائتماني</span><strong>{money(policy?.creditLimit)}</strong></div>
            <div className="debt-metric is-available"><span>المتبقي المتاح</span><strong>{money(policy?.available)}</strong></div>
            <div className="debt-metric is-debt"><span>مجموع الديون</span><strong>{money(policy?.outstanding)}</strong></div>
          </div>
          {immediate != null && immediate > 0 ? <p className="debt-note">سيُسجل {money(immediate)} دفعاً فورياً، و{money(deferred)} ديناً.</p> : null}
          {block ? <p className="status-error">{block}</p> : null}
          {message ? <p className={state === "Refused" || message.includes("تتجاوز") ? "status-error" : "debt-note"}>{message}</p> : null}
          {pending ? <p className="debt-pending">قيد التأكيد المركزي</p> : null}
          {pending ? <button type="button" onClick={() => void refresh()}>تحديث الحالة</button> : null}
          <div className="debt-table-card">
            <h3>سجل الديون والتسديدات للزبون</h3>
            {summary.stale ? <p>آخر رصيد متزامن. قد يكون غير محدث. المركز هو مصدر الرصيد.</p> : null}
            <table>
              <thead>
                <tr><th>#</th><th>التاريخ والوقت</th><th>نوع الحركة</th><th>رقم الطلبية</th><th>المبلغ</th><th>الرصيد بعد الحركة</th><th>الفرع</th><th>الموظف</th><th>ملاحظات</th><th>تفاصيل</th></tr>
              </thead>
              <tbody>
                {movements.map((row, index) => (
                  <tr key={row.operationId}>
                    <td>{index + 1}</td>
                    <td>{new Date(row.confirmedAt).toLocaleString("en-GB")}</td>
                    <td><span className={row.signedEffect < 0 ? "debt-pill is-pay" : row.eventType.includes("Reversal") ? "debt-pill is-reverse" : "debt-pill is-debt"}>{typeLabel[row.eventType] ?? row.eventType}</span></td>
                    <td>{row.reference || "—"}</td>
                    <td className={row.signedEffect < 0 ? "is-pay" : "is-debt"}>{row.signedEffect < 0 ? "-" : ""}{money(row.amount)}</td>
                    <td>{money(row.balanceAfter)}</td>
                    <td>{row.branchName || "—"}</td>
                    <td>{row.cashier || "—"}</td>
                    <td>{row.notes || "—"}</td>
                    <td><button type="button" onClick={() => void openDetails(row)}>تفاصيل</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            {skip + movements.length < totalCount ? <button type="button" onClick={() => void loadHistory(skip + 20)}>عرض المزيد</button> : null}
          </div>
          <div className="debt-footer">
            <p>لا يتم تعديل أو حذف أي سجل. التصحيح يتم بحركة جديدة.</p>
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
              <NumericKeypad
                allowDecimal
                onDigit={(digit) => pressDebtPad(digit)}
                onClear={() => pressDebtPad("Del")}
              />
            </section>
          </aside>
        ) : null}
      </div>
      <button type="button" className="debt-close" onClick={onBack}>إغلاق</button>
    </section>
  );

  function pressDebtPad(key: string) {
    if (key === "Enter") {
      void confirm();
      return;
    }
    if (key === "/" || key === "*" || key === "-" || key === "+") return;
    setTouched(true);
    if (key === "Del") {
      setDeferredInput("");
      return;
    }
    if (key === "BS") {
      setDeferredInput((current) => (current || String(total)).slice(0, -1));
      return;
    }
    setDeferredInput((current) => appendDeferred(current || String(total), key));
  }

  async function refresh() {
    const response = await fetch(`/api/pos/credit-sales/${operationId}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تعذر تحديث حالة العملية.");
      return;
    }
    const payload = await response.json() as { centrallyConfirmed?: boolean; reconciliationRequired?: boolean };
    if (payload.centrallyConfirmed) {
      setState("");
      setMessage("تم تأكيد الدين.");
      await loadHistory(0);
      onPosted();
    } else if (payload.reconciliationRequired) {
      setState("ReconciliationRequired");
      setMessage("العملية بحاجة إلى مراجعة مالية.");
    } else setMessage("العملية محفوظة محلياً وقيد التأكيد المركزي. ليست ديناً مؤكداً بعد.");
  }
}

function creditLineJson(line: CartDraft["lines"][number]) {
  const manual = line.priceOverrideMode === "UnitPrice"
    ? `,"priceOverrideMode":"UnitPrice","manualUnitPrice":${line.manualUnitPrice}`
    : line.priceOverrideMode === "LineTotal"
      ? `,"priceOverrideMode":"LineTotal","manualLineTotal":${line.manualLineTotal}`
      : `,"priceOverrideMode":"None"`;
  return line.lineType === "Fixed"
    ? `{"itemId":"${line.itemId}","quantity":${line.quantity},"lineType":"Fixed","expectedUnitPrice":${line.resolvedUnitPrice}${manual}}`
    : `{"itemId":"${line.referenceItemId}","quantity":${line.quantity},"lineType":"VariableValueByWeight","enteredScaleValue":${line.enteredScaleValue},"expectedFinalSellingPrice":${line.finalSellingPrice},"expectedReferenceBasePrice":${line.referenceBasePrice},"expectedWeightKg":${line.weightKg},"expectedStockOwnerQuantity":${line.projectedStockOwnerQuantity}${manual}}`;
}

function money(value: number | null | undefined) {
  if (value == null || Number.isNaN(Number(value))) return "—";
  return Number(value).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
