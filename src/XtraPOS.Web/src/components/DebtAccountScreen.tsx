import { useEffect, useState } from "react";
import { ArrowLeft } from "lucide-react";
import { deviceHeaders, SetupClientError } from "../api/client";
import { createCustomerClient, type CreditPolicy } from "../api/customers";
import { filterInvoices, invoiceLabel, repaidAmount, type DebtInvoice } from "../credit/debtAccount";
import type { DeviceSession } from "../types/setup";

export interface DebtCustomer {
  customerId: string;
  customerNumber: string | null;
  name: string;
  phone: string | null;
}

export function DebtAccountScreen({ session, customer, canCollect, onBack, onRepay }: {
  session: DeviceSession;
  customer: DebtCustomer;
  canCollect: boolean;
  onBack: () => void;
  onRepay: (invoiceId?: string) => void;
}) {
  const [policy, setPolicy] = useState<CreditPolicy | null>(null);
  const [rows, setRows] = useState<DebtInvoice[]>([]);
  const [unavailable, setUnavailable] = useState(false);
  const [branchId, setBranchId] = useState("");
  const [status, setStatus] = useState("");
  const [overdueOnly, setOverdueOnly] = useState(false);
  const [page, setPage] = useState(0);
  useEffect(() => {
    const headers = deviceHeaders(session);
    void Promise.all([
      createCustomerClient(session).creditPolicy(customer.customerId),
      fetch(`/api/pos/credit-collections/customers/${customer.customerId}/summary`, { headers }).then((response) => response.ok ? response.json() : Promise.reject(response.status)),
      fetch(`/api/pos/credit-collections/customers/${customer.customerId}/receivables`, { headers }).then((response) => response.ok ? response.json() : Promise.reject(response.status)),
    ]).then(([loaded, summary, body]) => {
      setPolicy({ ...loaded, outstanding: summary.outstanding, available: loaded.available });
      setRows((body.receivables ?? []).map((row: DebtInvoice) => ({ ...row, dueDate: String(row.dueDate) })));
    }).catch((error: unknown) => {
      setUnavailable(true);
      setPolicy(null);
      if (!(error instanceof SetupClientError)) setRows([]);
    });
  }, [customer.customerId, session]);
  const visible = filterInvoices(rows, { branchId: branchId || undefined, status: status || undefined, overdueOnly });
  const pageRows = visible.slice(page * 20, page * 20 + 20);
  const overdue = rows.filter((row) => row.dueDate.slice(0, 10) < new Date().toISOString().slice(0, 10)).reduce((sum, row) => sum + row.outstandingAmount, 0);
  return (
    <section className="new-customer" dir="rtl" aria-label="حساب ديون الزبون">
      <header className="new-customer-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <h2>حساب ديون الزبون</h2>
        <button type="button" className="new-customer-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      <div className="new-customer-card credit-sale-sheet">
        {unavailable ? <p className="status-error">جهة الائتمان المركزية غير متاحة. لا يُعرض رصيد محفوظ كرصيد حي.</p> : null}
        <p>الرقم {customer.customerNumber ?? "—"} · {customer.name} · {customer.phone ?? "—"}</p>
        <p>الحالة {policy?.creditStatus ?? "—"} · الحد {policy?.creditLimit ?? "—"} NIS · الدين القائم {policy?.outstanding ?? "—"} NIS</p>
        <p>المتأخر {overdue} NIS · المتاح {policy?.available ?? "—"} NIS · أقرب استحقاق {rows[0]?.dueDate.slice(0, 10) ?? "—"}</p>
        <label>فرع المصدر<input aria-label="فرع المصدر" value={branchId} onChange={(event) => { setBranchId(event.target.value); setPage(0); }} /></label>
        <label>الحالة<input aria-label="حالة الفاتورة" value={status} onChange={(event) => { setStatus(event.target.value); setPage(0); }} /></label>
        <label><input type="checkbox" checked={overdueOnly} onChange={(event) => setOverdueOnly(event.target.checked)} />المتأخر فقط</label>
        <table>
          <thead><tr><th>المرجع</th><th>فرع البيع</th><th>الاستحقاق</th><th>الأصل</th><th>المسدد</th><th>المتبقي</th><th>الحالة</th><th></th></tr></thead>
          <tbody>
            {pageRows.map((row) => (
              <tr key={row.receivableId}>
                <td>{invoiceLabel(row)}</td>
                <td>{row.sourceBranchName ?? "فرع المصدر غير معروف"}</td>
                <td>{row.dueDate.slice(0, 10)}</td>
                <td>{row.originalAmount}</td>
                <td>{repaidAmount(row)}</td>
                <td>{row.outstandingAmount}</td>
                <td>{row.status}</td>
                <td><button type="button" disabled={!canCollect} onClick={() => onRepay(row.receivableId)}>تسديد</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button type="button" disabled={page === 0} onClick={() => setPage((current) => current - 1)}>السابق</button>
        <button type="button" disabled={(page + 1) * 20 >= visible.length} onClick={() => setPage((current) => current + 1)}>التالي</button>
        <button type="button" className="primary-button" disabled={!canCollect || unavailable} onClick={() => onRepay()}>تسديد دين</button>
      </div>
    </section>
  );
}
