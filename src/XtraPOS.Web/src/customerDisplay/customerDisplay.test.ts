import { describe, expect, it } from "vitest";
import type { CartDraft, FixedCartLine } from "../cart/model";
import { buildDisplayState, type DisplayInput } from "./customerDisplay";

function line(id: string, name: string, quantity: string, unitPrice: string, original = unitPrice): FixedCartLine {
  const total = (Number(quantity) * Number(unitPrice)).toFixed(2);
  return {
    cartLineId: id, lineType: "Fixed", itemId: id, itemNumber: id, itemName: name, quantity, customerId: null,
    priceTypeId: "p", priceTypeCode: "Price0", unitPrice, originalUnitPrice: original, priceSource: "Default",
    resolvedUnitPrice: unitPrice, resolvedPriceSource: "Default", priceOverrideMode: "None", manualUnitPrice: null,
    manualLineTotal: null, lineTotal: total, pricingMode: "Fixed", pricingResolvedAt: "2026-11-21T00:00:00Z",
  } as FixedCartLine;
}

function cart(lines: FixedCartLine[]): CartDraft {
  return { cartId: "c1", status: "Active", scope: { businessId: "b", branchId: "br", stationId: "s", deviceId: "d", userId: "u" }, customerId: "cust", lines, failedLineIds: [] } as CartDraft;
}

const base: DisplayInput = {
  business: "Smoker World", branch: "Labtop", cart: null, customerName: null, paying: false, payMethod: "Cash",
  tendered: "", cashPart: "", creditOpen: false, deferred: "", receipt: null, receiptAt: 0, memberQr: "data:member", invoiceQr: "",
};

describe("customer display state", () => {
  it("welcomes with the membership code when there is no sale", () => {
    expect(buildDisplayState(base)).toEqual({ phase: "idle", business: "Smoker World", branch: "Labtop", memberQr: "data:member" });
  });

  it("lists the items, the last item, the discount, and the total", () => {
    const state = buildDisplayState({ ...base, customerName: "test5000", cart: cart([line("1", "مياه", "3", "3"), line("2", "ولاعة", "2", "5", "6")]) });
    expect(state.phase).toBe("sale");
    if (state.phase !== "sale") return;
    expect(state.customerName).toBe("test5000");
    expect(state.lastLineId).toBe("2");
    expect(state.pieces).toBe("5");
    expect(state.beforeDiscount).toBe("21.00");
    expect(state.discount).toBe("2.00");
    expect(state.total).toBe("19.00");
    expect(JSON.stringify(state)).not.toMatch(/cost|profit|limit|balance/i);
  });

  it("shows the change while the cashier types the cash received, even half typed", () => {
    const sale = cart([line("1", "مياه", "3", "3")]);
    const typed = buildDisplayState({ ...base, cart: sale, paying: true, tendered: "20" });
    expect(typed).toMatchObject({ phase: "payment", total: "9.00", paid: "20.00", change: "11.00" });
    expect(buildDisplayState({ ...base, cart: sale, paying: true, tendered: "2." })).toMatchObject({ phase: "payment", paid: "0.00", change: "0.00" });
  });

  it("shows only the deferred amount for a credit sale", () => {
    const state = buildDisplayState({ ...base, cart: cart([line("1", "قهوة", "1", "32")]), creditOpen: true, deferred: "20" });
    expect(state).toMatchObject({ phase: "deferred", total: "32.00", deferred: "20.00", paidNow: "12.00" });
  });

  it("thanks the customer with the invoice code when there is one, else the membership code", () => {
    const receipt = { saleId: "s1", saleNumber: 1042, total: "9", idempotentReplay: false, payments: [{ method: "Cash", amount: "9", tenderedAmount: "10", changeAmount: "1" }] };
    const now = Date.now();
    expect(buildDisplayState({ ...base, receipt, receiptAt: now })).toMatchObject({ phase: "thanks", saleNumber: 1042, change: "1.00", qr: "data:member" });
    expect(buildDisplayState({ ...base, receipt, receiptAt: now, invoiceQr: "data:invoice" })).toMatchObject({ qr: "data:invoice", qrLabel: "امسح الرمز لفتح فاتورتك على هاتفك" });
    expect(buildDisplayState({ ...base, receipt, receiptAt: now - 60_000 }).phase).toBe("idle");
  });
});
