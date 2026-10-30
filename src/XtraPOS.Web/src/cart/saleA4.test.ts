import { describe, expect, it } from "vitest";
import { createCart } from "./model";
import { buildA4Document, publicHttpsBase, whatsAppShareUrl } from "./saleA4";

const scope = { businessId: "b", branchId: "br", stationId: "s", deviceId: "d", userId: "u" };

describe("A4 sale document", () => {
  it("does not build a document for an empty cart", () => {
    expect(buildA4Document({ cart: createCart(scope), businessName: "أ", branchName: "ب", cashierName: "ج", customerNumber: null, customerName: null })).toBeNull();
  });

  it("prints the current lines as an unposted order and keeps the cart", () => {
    const cart = createCart(scope);
    cart.lines = [{
      cartLineId: "1", lineType: "Fixed", itemId: "i", itemNumber: "10", itemName: "حليب طويل الاسم", quantity: "1.5",
      customerId: null, priceTypeId: "p", priceTypeCode: "Price0", unitPrice: "4", originalUnitPrice: "5", priceSource: "Special",
      resolvedUnitPrice: "4", resolvedPriceSource: "Special", priceOverrideMode: "None", manualUnitPrice: null, manualLineTotal: null,
      lineTotal: "6", pricingMode: "Fixed", pricingResolvedAt: "2026-10-26T00:00:00Z",
    }];
    const sheet = buildA4Document({ cart, businessName: "سموكر", branchName: "نابلس", cashierName: "سليم", customerNumber: "7", customerName: "Test9" });
    expect(sheet?.draft).toBe(true);
    expect(sheet?.title).toBe("أمر بيع غير مكتمل");
    expect(sheet?.lines[0].name).toBe("حليب طويل الاسم");
    expect(sheet?.total).toBe("6");
    expect(cart.lines).toHaveLength(1);
  });

  it("builds a WhatsApp link and rejects private bases", () => {
    const url = whatsAppShareUrl("0592594707", "https://invoices.example/a");
    expect(url.startsWith("https://wa.me/972592594707?text=")).toBe(true);
    expect(decodeURIComponent(url)).toContain("https://invoices.example/a");
    expect(whatsAppShareUrl(null, "https://invoices.example/a").startsWith("https://wa.me/?text=")).toBe(true);
    expect(publicHttpsBase("http://192.168.1.5")).toBeNull();
    expect(publicHttpsBase("https://invoices.example")).toBe("https://invoices.example");
  });
});
