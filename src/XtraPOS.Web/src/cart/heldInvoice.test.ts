import { describe, expect, it } from "vitest";
import { createCart } from "./model";
import { cartAfterSuccessfulHold, cartFromHeldSale, claimSingleHold, currentInvoiceNeedsGuard, holdSaveRequest } from "./heldInvoice";

const scope = { businessId: "b", branchId: "br", stationId: "s", deviceId: "d", userId: "u" };

function cartWithLine() {
  const cart = createCart(scope, "cart-1");
  cart.customerId = "customer-1";
  cart.lines = [{
    cartLineId: "line-1",
    lineType: "Fixed",
    itemId: "item-1",
    itemNumber: "10",
    itemName: "حليب",
    quantity: "2",
    customerId: "customer-1",
    priceTypeId: "p",
    priceTypeCode: "Price0",
    unitPrice: "5.50",
    originalUnitPrice: "5.50",
    priceSource: "Price0",
    resolvedUnitPrice: "5.50",
    resolvedPriceSource: "Price0",
    priceOverrideMode: "None",
    manualUnitPrice: null,
    manualLineTotal: null,
    lineTotal: "11.00",
    pricingMode: "Fixed",
    pricingResolvedAt: "2026-10-26T00:00:00Z",
  }];
  return cart;
}

describe("held invoices", () => {
  it("refuses an empty invoice and keeps the caller from opening a new one", () => {
    const cart = createCart(scope);
    expect(holdSaveRequest(cart)).toEqual({ error: "لا توجد أصناف لحفظها." });
    expect(cart.lines).toHaveLength(0);
  });

  it("saves the customer and lines, then opens a new invoice only after success", () => {
    const cart = cartWithLine();
    const request = holdSaveRequest(cart);
    expect("lines" in request && request.lines[0].quantity).toBe("2");
    expect("customerId" in request && request.customerId).toBe("customer-1");
    const next = cartAfterSuccessfulHold(scope);
    expect(next.lines).toHaveLength(0);
    expect(next.customerId).toBeNull();
    expect(next.cartId).not.toBe(cart.cartId);
    expect(cart.lines).toHaveLength(1);
  });

  it("updates the same held sale instead of creating another id", () => {
    const cart = cartWithLine();
    cart.heldSaleId = "sale-1";
    cart.heldCartId = "held-cart";
    cart.heldRowVersion = "v1";
    const request = holdSaveRequest(cart);
    expect(request).toMatchObject({ saleId: "sale-1", cartId: "held-cart", rowVersion: "v1" });
  });

  it("ignores a repeated save while the first save is still running", () => {
    const state = { busy: false };
    expect(claimSingleHold(state)).toBe(true);
    expect(claimSingleHold(state)).toBe(false);
  });

  it("restores lines without dropping the held sale and guards a current invoice", () => {
    const current = cartWithLine();
    expect(currentInvoiceNeedsGuard(current)).toBe(true);
    const restored = cartFromHeldSale(scope, {
      saleId: "sale-9",
      cartId: "held-cart",
      customerId: "customer-1",
      rowVersion: "v2",
      lines: [{ lineType: "Fixed", itemId: "item-1", itemNumber: "10", itemName: "حليب", quantity: "2", unitPrice: "5.50", lineTotal: "11.00", priceSource: "Price0", priceOverrideMode: "None", priceTypeId: "p", priceTypeCode: "Price0", manualUnitPrice: null, manualLineTotal: null }],
    });
    expect(restored.heldSaleId).toBe("sale-9");
    expect(restored.lines[0].lineType === "Fixed" && restored.lines[0].quantity).toBe("2");
    expect(restored.cartId).not.toBe("held-cart");
  });
});
