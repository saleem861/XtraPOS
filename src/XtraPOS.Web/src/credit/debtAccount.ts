export interface DebtInvoice {
  receivableId: string;
  sourceSaleId?: string;
  sourceBranchId: string;
  sourceBranchName?: string | null;
  sourceSaleNumber?: number | null;
  originalAmount: number;
  outstandingAmount: number;
  dueDate: string;
  status: string;
}

export function invoiceLabel(invoice: Pick<DebtInvoice, "receivableId" | "sourceSaleNumber">): string {
  return invoice.sourceSaleNumber != null ? `فاتورة ${invoice.sourceSaleNumber}` : `مرجع الذمة ${invoice.receivableId}`;
}

export interface AllocationLine {
  receivableId: string;
  amount: number;
}

export function repaidAmount(invoice: DebtInvoice): number {
  return Math.round((invoice.originalAmount - invoice.outstandingAmount) * 10000) / 10000;
}

export function filterInvoices(invoices: DebtInvoice[], filter: { branchId?: string; status?: string; overdueOnly?: boolean; today?: string }): DebtInvoice[] {
  const today = filter.today ?? new Date().toISOString().slice(0, 10);
  return invoices.filter((invoice) => {
    if (filter.branchId && invoice.sourceBranchId !== filter.branchId) return false;
    if (filter.status && invoice.status !== filter.status) return false;
    if (filter.overdueOnly && invoice.dueDate.slice(0, 10) >= today) return false;
    return true;
  });
}

export function previewAllocation(invoices: DebtInvoice[], amount: number, selectedId?: string | null): { lines: AllocationLine[]; remaining: number; error: string | null } {
  const pool = (selectedId ? invoices.filter((invoice) => invoice.receivableId === selectedId) : [...invoices].sort((a, b) => a.dueDate.localeCompare(b.dueDate) || a.receivableId.localeCompare(b.receivableId)))
    .filter((invoice) => invoice.outstandingAmount > 0);
  const collectible = pool.reduce((sum, invoice) => sum + invoice.outstandingAmount, 0);
  if (!(amount > 0) || amount > collectible + 0.0000001) {
    return { lines: [], remaining: collectible, error: "المبلغ يتجاوز الدين القابل للتحصيل." };
  }
  let left = amount;
  const lines: AllocationLine[] = [];
  for (const invoice of pool) {
    if (left <= 0) break;
    const take = Math.min(invoice.outstandingAmount, left);
    lines.push({ receivableId: invoice.receivableId, amount: Math.round(take * 10000) / 10000 });
    left = Math.round((left - take) * 10000) / 10000;
  }
  return { lines, remaining: Math.round((collectible - amount) * 10000) / 10000, error: null };
}
