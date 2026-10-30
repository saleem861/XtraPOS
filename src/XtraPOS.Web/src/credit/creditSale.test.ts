import { deferredAmount, creditEligible } from "./creditSale";
import { describe, expect, it } from "vitest";

describe("credit sale amounts", () => {
  it("splits a partial payment and rejects a zero or negative deferral", () => {
    expect(deferredAmount({ invoiceTotal: 600, immediatePayment: 200 })).toEqual({ deferred: 400 });
    expect(deferredAmount({ invoiceTotal: 600, immediatePayment: 0 })).toEqual({ deferred: 600 });
    expect("error" in deferredAmount({ invoiceTotal: 600, immediatePayment: 600 })).toBe(true);
    expect("error" in deferredAmount({ invoiceTotal: 600, immediatePayment: 700 })).toBe(true);
  });

  it("rejects walk-in, disabled, suspended, and exhausted credit", () => {
    const active = { creditEnabled: true, creditStatus: "Active", available: 100, centralReachable: true };
    expect(creditEligible(active, null, true)).toContain("زبون عادي");
    expect(creditEligible({ ...active, creditEnabled: false }, "c", true)).toContain("غير نشط");
    expect(creditEligible({ ...active, creditStatus: "Suspended" }, "c", true)).toContain("غير نشط");
    expect(creditEligible({ ...active, available: 0 }, "c", true)).toContain("لا يوجد");
    expect(creditEligible(active, "c", false)).toContain("صلاحية");
    expect(creditEligible({ ...active, centralReachable: false }, "c", true)).toContain("غير متاحة");
    expect(creditEligible(active, "c", true)).toBeNull();
  });
});
