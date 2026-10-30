import type { CartDraft, CartLine, CartScope, FixedCartLine, VariableCartLine } from "./model";
import { createCart, createClientId } from "./model";

export interface HeldLinePayload {
  lineType: string;
  itemId: string;
  quantity: string;
  unitPrice: string;
  lineTotal: string;
  priceOverrideMode: string;
  stockOwnerItemId?: string;
  enteredScaleValue?: string;
  referenceBasePrice?: string;
  weightKg?: string;
  weightGrams?: string;
  conversionFactor?: string;
  stockOwnerQuantity?: string;
  discountPercent?: string;
  finalSellingPrice?: string;
  priceSource?: string;
}

export function holdSaveRequest(cart: CartDraft): { cartId: string; saleId: string | null; rowVersion: string | null; customerId: string | null; lines: HeldLinePayload[] } | { error: string } {
  if (cart.lines.length === 0) return { error: "لا توجد أصناف لحفظها." };
  return {
    cartId: cart.heldCartId || createClientId(),
    saleId: cart.heldSaleId || null,
    rowVersion: cart.heldRowVersion || null,
    customerId: cart.customerId,
    lines: cart.lines.map(toPayload),
  };
}

export function cartAfterSuccessfulHold(scope: CartScope): CartDraft {
  return createCart(scope);
}

export function cartFromHeldSale(scope: CartScope, sale: {
  saleId: string;
  cartId: string;
  customerId: string | null;
  rowVersion: string;
  lines: Array<Record<string, string | number | null>>;
}): CartDraft {
  const cart = createCart(scope);
  return {
    ...cart,
    customerId: sale.customerId,
    heldSaleId: sale.saleId,
    heldCartId: sale.cartId,
    heldRowVersion: sale.rowVersion,
    lines: sale.lines.map(toLine),
  };
}

export function claimSingleHold(state: { busy: boolean }): boolean {
  if (state.busy) return false;
  state.busy = true;
  return true;
}

export function currentInvoiceNeedsGuard(cart: CartDraft): boolean {
  return cart.lines.length > 0;
}

function toPayload(line: CartLine): HeldLinePayload {
  if (line.lineType === "Fixed") {
    return {
      lineType: "Fixed",
      itemId: line.itemId,
      quantity: line.quantity,
      unitPrice: line.priceOverrideMode === "None" ? line.resolvedUnitPrice : (line.manualUnitPrice || line.resolvedUnitPrice),
      lineTotal: line.lineTotal,
      priceOverrideMode: line.priceOverrideMode,
      priceSource: line.priceSource,
    };
  }
  return {
    lineType: "VariableValueByWeight",
    itemId: line.referenceItemId,
    quantity: line.quantity,
    unitPrice: line.finalSellingPrice,
    lineTotal: line.lineTotal,
    priceOverrideMode: line.priceOverrideMode,
    stockOwnerItemId: line.stockOwnerItemId,
    enteredScaleValue: line.enteredScaleValue,
    referenceBasePrice: line.referenceBasePrice,
    weightKg: line.weightKg,
    weightGrams: line.weightGrams,
    conversionFactor: line.stockOwnerConversionFactor,
    stockOwnerQuantity: line.projectedStockOwnerQuantity,
    discountPercent: line.variableValueDiscountPercent,
    finalSellingPrice: line.finalSellingPrice,
    priceSource: line.referencePriceSource,
  };
}

function toLine(raw: Record<string, string | number | null>): CartLine {
  const text = (key: string) => String(raw[key] ?? "");
  if (text("lineType") === "VariableValueByWeight") {
    const line: VariableCartLine = {
      cartLineId: createClientId(),
      lineType: "VariableValueByWeight",
      referenceItemId: text("itemId"),
      referenceItemNumber: text("itemNumber"),
      referenceItemName: text("itemName"),
      stockOwnerItemId: text("stockOwnerItemId"),
      stockOwnerItemNumber: text("stockOwnerItemNumber"),
      enteredScaleValue: text("enteredScaleValue"),
      referenceBasePrice: text("referenceBasePrice"),
      referencePriceSource: text("priceSource"),
      weightKg: text("weightKg"),
      weightGrams: text("weightGrams"),
      stockOwnerConversionFactor: text("conversionFactor") || "1",
      projectedStockOwnerQuantity: text("stockOwnerQuantity"),
      variableValueDiscountPercent: text("discountPercent"),
      rawDiscountedValue: text("unitPrice"),
      finalSellingPrice: text("finalSellingPrice") || text("unitPrice"),
      roundingPolicy: "",
      projectedCost: null,
      costSource: "",
      branchId: "",
      quantity: text("quantity"),
      customerId: null,
      unitPrice: text("unitPrice"),
      resolvedUnitPrice: text("unitPrice"),
      resolvedPriceSource: text("priceSource"),
      priceOverrideMode: (text("priceOverrideMode") || "None") as VariableCartLine["priceOverrideMode"],
      manualUnitPrice: raw.manualUnitPrice == null ? null : text("manualUnitPrice"),
      manualLineTotal: raw.manualLineTotal == null ? null : text("manualLineTotal"),
      lineTotal: text("lineTotal"),
      pricingResolvedAt: new Date(0).toISOString(),
    };
    return line;
  }
  const fixed: FixedCartLine = {
    cartLineId: createClientId(),
    lineType: "Fixed",
    itemId: text("itemId"),
    itemNumber: text("itemNumber"),
    itemName: text("itemName"),
    quantity: text("quantity"),
    customerId: null,
    priceTypeId: text("priceTypeId"),
    priceTypeCode: text("priceTypeCode"),
    unitPrice: text("unitPrice"),
    originalUnitPrice: text("unitPrice"),
    priceSource: text("priceSource"),
    resolvedUnitPrice: text("unitPrice"),
    resolvedPriceSource: text("priceSource"),
    priceOverrideMode: (text("priceOverrideMode") || "None") as FixedCartLine["priceOverrideMode"],
    manualUnitPrice: raw.manualUnitPrice == null ? null : text("manualUnitPrice"),
    manualLineTotal: raw.manualLineTotal == null ? null : text("manualLineTotal"),
    lineTotal: text("lineTotal"),
    pricingMode: "Fixed",
    pricingResolvedAt: new Date(0).toISOString(),
  };
  return fixed;
}
