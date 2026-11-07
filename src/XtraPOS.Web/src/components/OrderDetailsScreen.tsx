import { Printer, X } from "lucide-react";
import { useState } from "react";
import type { A4Document } from "../cart/saleA4";
import { InvoicePreview } from "./SaleA4Sheet";
import { ScreenHeader } from "./ScreenHeader";
import "./debtScreens.css";

export interface DebtDocument {
  kind?: string;
  saleNumber?: string | null;
  customerNumber?: string | null;
  customerName?: string | null;
  branchName?: string | null;
  posNumber?: number | null;
  cashier?: string | null;
  when?: string | null;
  subtotal?: number;
  discount?: number;
  total?: number;
  immediate?: number;
  deferred?: number;
  notes?: string | null;
  amount?: number;
  lines?: Array<{ number?: number; name?: string; unit?: string; quantity?: number; unitPrice?: number; discount?: number; total?: number }>;
  allocations?: Array<{ receivableId?: string; amount?: number }>;
}

export function OrderDetailsScreen({
  title,
  document: sheet,
  onClose,
}: {
  title: string;
  document: DebtDocument;
  onClose: () => void;
}) {
  const [preview, setPreview] = useState<A4Document | null>(null);
  const [printingReceipt, setPrintingReceipt] = useState(false);
  const lines = sheet.lines ?? [];
  const a4 = toA4(title, sheet);
  return (
    <section className={printingReceipt ? "new-customer debt-screen is-printing-receipt" : preview ? "new-customer debt-screen is-printing-a4" : "new-customer debt-screen"} dir="rtl" aria-label={title}>
      <ScreenHeader title={title} onBack={onClose} />
      <div className="debt-details">
        <div className="debt-facts">
          <Fact icon="user" label="اسم الزبون" value={sheet.customerName || "—"} />
          <Fact icon="id" label="رقم الزبون" value={sheet.customerNumber || "—"} />
          <Fact icon="date" label="تاريخ الطلبية" value={sheet.when ? new Date(sheet.when).toLocaleString("en-GB") : "—"} />
          <Fact icon="doc" label="رقم الطلبية" value={sheet.saleNumber || "—"} />
          <Fact icon="ok" label="حالة الطلبية" value="مكتملة" />
          <Fact icon="sum" label="المجموع بعد الخصم" value={money(sheet.total)} />
        </div>
        {lines.length > 0 ? (
          <div className="debt-table-card">
            <h3>تفاصيل الأصناف</h3>
            <table>
              <thead>
                <tr><th>#</th><th>الصنف</th><th>الوحدة</th><th>الكمية</th><th>سعر الوحدة</th><th>الخصم</th><th>الإجمالي</th></tr>
              </thead>
              <tbody>
                {lines.map((line, index) => (
                  <tr key={`${line.number ?? index}`}>
                    <td>{line.number ?? index + 1}</td>
                    <td>{line.name}</td>
                    <td>{line.unit || "—"}</td>
                    <td>{line.quantity}</td>
                    <td>{money(line.unitPrice)}</td>
                    <td>{money(line.discount)}</td>
                    <td>{money(line.total)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="debt-table-card">
            <h3>تفاصيل سند التسديد</h3>
            <p>المبلغ {money(sheet.amount ?? sheet.total)} · الصندوق {sheet.cashier || "—"} · الفرع {sheet.branchName || "—"}</p>
            {(sheet.allocations ?? []).map((row) => <p key={row.receivableId}>تخصيص {money(row.amount)}</p>)}
          </div>
        )}
        <div className="debt-totals">
          <Fact icon="sum" label="الإجمالي قبل الخصم" value={money(sheet.subtotal)} />
          <Fact icon="tag" label="قيمة الخصم" value={money(sheet.discount)} tone="danger" />
          <Fact icon="sum" label="المجموع النهائي" value={money(sheet.total)} tone="strong" />
          <Fact icon="pay" label="طريقة الدفع" value={sheet.kind === "receipt" ? "تسديد نقدي" : "تأجيل دفع"} />
          <Fact icon="note" label="ملاحظات" value={sheet.notes || "—"} />
        </div>
        <p className="debt-pay-split">المدفوع فوراً {money(sheet.immediate)} · المؤجل {money(sheet.deferred)}</p>
        <div className="debt-actions">
          <button type="button" className="debt-print" onClick={() => setPreview(a4)}><Printer aria-hidden="true" />طباعة A4</button>
          <button type="button" className="debt-print is-receipt" onClick={() => { setPrintingReceipt(true); window.setTimeout(() => { window.print(); setPrintingReceipt(false); }, 50); }}><Printer aria-hidden="true" />طباعة وصل</button>
          <button type="button" className="debt-close" onClick={onClose}><X aria-hidden="true" />إغلاق</button>
        </div>
      </div>
      <article className="debt-receipt-sheet" aria-hidden={!printingReceipt}>
        <h1>وصل {sheet.kind === "receipt" ? "تسديد" : "بيع"}</h1>
        <p>{sheet.customerName || "—"} · {sheet.customerNumber || "—"}</p>
        <p>{sheet.saleNumber || "سند"} · {sheet.branchName || "—"}</p>
        <p>المبلغ {money(sheet.kind === "receipt" ? (sheet.amount ?? sheet.total) : sheet.deferred)}</p>
        <p>{sheet.notes || "—"}</p>
      </article>
      {preview ? (
        <InvoicePreview
          document={preview}
          shareUrl={null}
          branchName={sheet.branchName || "—"}
          stationName={sheet.posNumber ? `نقطة ${sheet.posNumber}` : "—"}
          userName={sheet.cashier || "—"}
          onPrint={() => window.print()}
          onWhatsApp={() => undefined}
          onBack={() => setPreview(null)}
        />
      ) : null}
    </section>
  );
}

function toA4(title: string, sheet: DebtDocument): A4Document {
  return {
    title,
    businessName: "XtraPOS",
    branchName: sheet.branchName || "—",
    cashierName: sheet.cashier || "—",
    customerNumber: sheet.customerNumber || "—",
    customerName: sheet.customerName || "—",
    when: sheet.when ? new Date(sheet.when).toLocaleString("en-GB") : "—",
    reference: sheet.saleNumber ?? null,
    lines: (sheet.lines ?? []).map((line, index) => ({
      number: String(line.number ?? index + 1),
      name: line.name || "—",
      quantity: String(line.quantity ?? ""),
      unitPrice: money(line.unitPrice),
      discount: money(line.discount),
      total: money(line.total),
    })),
    itemCount: sheet.lines?.length ?? 0,
    discountTotal: money(sheet.discount),
    total: money(sheet.total ?? sheet.amount),
    draft: false,
  };
}

function Fact({ label, value, tone }: { icon?: string; label: string; value: string; tone?: "danger" | "strong" }) {
  return <div className={tone ? `debt-fact is-${tone}` : "debt-fact"}><span>{label}</span><strong>{value}</strong></div>;
}

function money(value: number | null | undefined) {
  if (value == null || Number.isNaN(Number(value))) return "—";
  return Number(value).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
