import { describe, expect, it } from "vitest";
import { filterInvoices, invoiceLabel, previewAllocation, repaidAmount, type DebtInvoice } from "./debtAccount";

const invoices: DebtInvoice[] = [
  { receivableId: "a", sourceBranchId: "b1", originalAmount: 200, outstandingAmount: 200, dueDate: "2026-01-01", status: "Open" },
  { receivableId: "b", sourceBranchId: "b2", originalAmount: 350, outstandingAmount: 350, dueDate: "2026-03-01", status: "Open" },
];

describe("debt account", () => {
  it("previews FIFO and a selected invoice", () => {
    const fifo = previewAllocation(invoices, 300);
    expect(fifo.lines).toEqual([{ receivableId: "a", amount: 200 }, { receivableId: "b", amount: 100 }]);
    expect(fifo.remaining).toBe(250);
    const selected = previewAllocation(invoices, 50, "b");
    expect(selected.lines).toEqual([{ receivableId: "b", amount: 50 }]);
    expect(previewAllocation(invoices, 900).error).toBeTruthy();
    expect(repaidAmount({ ...invoices[0], outstandingAmount: 80 })).toBe(120);
    expect(invoiceLabel({ receivableId: "abc", sourceSaleNumber: 41 })).toBe("فاتورة 41");
    expect(invoiceLabel({ receivableId: "abc", sourceSaleNumber: null })).toBe("مرجع الذمة abc");
  });

  it("filters by origin branch and overdue status", () => {
    expect(filterInvoices(invoices, { branchId: "b2" })).toHaveLength(1);
    expect(filterInvoices(invoices, { overdueOnly: true, today: "2026-02-01" }).map((row) => row.receivableId)).toEqual(["a"]);
  });
});
