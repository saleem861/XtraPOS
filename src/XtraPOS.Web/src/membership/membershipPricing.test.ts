import { describe, expect, it } from "vitest";
import { addFixedLine, cartTotals, createCart, overrideUnitPrice, repriceCart, type CartScope, type FixedQuote, type PricingGateway } from "../cart/model";
import { membershipPricingCustomer } from "./membershipPricing";

describe("membershipPricingCustomer", () => {
  it("keeps special prices off until verification when the gate is on", () => {
    expect(membershipPricingCustomer(true, false, "customer-1")).toBeNull();
    expect(membershipPricingCustomer(true, true, "customer-1")).toBe("customer-1");
  });

  it("leaves ordinary customer pricing in place when the gate is off", () => {
    expect(membershipPricingCustomer(false, false, "customer-1")).toBe("customer-1");
    expect(membershipPricingCustomer(true, true, null)).toBeNull();
  });

  it("reprices the open cart after verification and keeps a manual override", async () => {
    const scope: CartScope = { businessId: "b", branchId: "br", stationId: "s", deviceId: "d", userId: "u" };
    const quote = (unitPrice: string): FixedQuote => ({
      itemId: "item-1",
      itemNumber: "100",
      itemName: "صنف",
      priceTypeId: "price",
      priceTypeCode: unitPrice === "4" ? "Price2" : "Price0",
      basePrice: "10",
      unitPrice,
      priceSource: "Customer",
      pricingResolvedAt: "2026-11-03T00:00:00Z",
    });
    const gateway: PricingGateway = {
      resolveFixed: async (_item, customerId) => quote(customerId ? "4" : "10"),
      resolveVariable: async () => { throw new Error("unused"); },
    };
    const before = addFixedLine(createCart(scope), quote("10"), "2", "line-1");
    expect(membershipPricingCustomer(true, false, "customer-1")).toBeNull();
    const blocked = await repriceCart(before, null, gateway);
    expect(cartTotals(blocked).total).toBe("20");
    const entitled = membershipPricingCustomer(true, true, "customer-1");
    const priced = await repriceCart(blocked, entitled, gateway);
    expect(priced.customerId).toBe("customer-1");
    expect(cartTotals(priced).total).toBe("8");
    const manual = await repriceCart(overrideUnitPrice(priced, "line-1", "3"), entitled, gateway);
    const line = manual.lines[0];
    expect(line?.priceOverrideMode).toBe("UnitPrice");
    expect(line?.unitPrice).toBe("3");
    expect(cartTotals(manual).total).toBe("6");
    const switched = await repriceCart(manual, membershipPricingCustomer(true, false, "customer-2"), gateway);
    expect(switched.customerId).toBeNull();
    expect(switched.lines[0]?.unitPrice).toBe("3");
  });
});