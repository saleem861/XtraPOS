import { deferredAmount, splitDeferred, appendDeferred, creditEligible, creditLimitMessage } from "./creditSale";
import { describe, expect, it } from "vitest";

describe("credit sale amounts", () => {
  it("splits a partial payment and rejects a zero or negative deferral", () => {
    expect(deferredAmount({ invoiceTotal: 600, immediatePayment: 200 })).toEqual({ deferred: 400 });
    expect(deferredAmount({ invoiceTotal: 600, immediatePayment: 0 })).toEqual({ deferred: 600 });
    expect(splitDeferred(600, 400)).toEqual({ deferred: 400, immediate: 200 });
    expect("error" in splitDeferred(600, 0)).toBe(true);
    expect("error" in splitDeferred(600, 700)).toBe(true);
    expect(appendDeferred("12", "3")).toBe("123");
    expect(appendDeferred("12.", "5")).toBe("12.5");
    expect("error" in deferredAmount({ invoiceTotal: 600, immediatePayment: 600 })).toBe(true);
    expect("error" in deferredAmount({ invoiceTotal: 600, immediatePayment: 700 })).toBe(true);
  });

  it("rejects walk-in, disabled, suspended, and exhausted credit", () => {
    const active = { creditEnabled: true, creditStatus: "Active", available: 100, centralReachable: true };
    expect(creditEligible(active, null, true)).toContain("زبون عادي");
    expect(creditEligible({ ...active, creditEnabled: false }, "c", true)).toContain("غير نشط");
    expect(creditLimitMessage(40.5, 50)).toContain("تتجاوز الحد");
    expect(creditLimitMessage(50, 50)).toBeNull();
    expect(creditEligible({ ...active, creditStatus: "Suspended" }, "c", true)).toContain("غير نشط");
    expect(creditEligible({ ...active, available: 0 }, "c", true)).toContain("لا يوجد");
    expect(creditEligible(active, "c", false)).toContain("صلاحية");
    expect(creditEligible({ ...active, centralReachable: false }, "c", true)).toContain("غير متاحة");
    expect(creditEligible(active, "c", true)).toBeNull();
  });
});
