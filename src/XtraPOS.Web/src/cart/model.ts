import { addDecimals, decimalEquals, divideDecimals, isPositiveDecimal, isPositiveInteger, multiplyDecimals, roundDecimal } from "./decimal";

export type PriceOverrideMode = "None" | "UnitPrice" | "LineTotal";

export interface CartScope {
  businessId: string;
  branchId: string;
  stationId: string;
  deviceId: string;
  userId: string;
}

export interface FixedQuote {
  itemId: string;
  itemNumber: string;
  itemName: string;
  priceTypeId: string;
  priceTypeCode: string;
  basePrice: string;
  unitPrice: string;
  priceSource: string;
  pricingResolvedAt: string;
}

export interface VariableQuote {
  referenceItemId: string;
  referenceItemNumber: string;
  referenceItemName: string;
  stockOwnerItemId: string;
  stockOwnerItemNumber: string;
  enteredScaleValue: string;
  referenceBasePrice: string;
  referencePriceSource: string;
  weightKg: string;
  weightGrams: string;
  stockOwnerConversionFactor: string;
  projectedStockOwnerQuantity: string;
  variableValueDiscountPercent: string;
  rawDiscountedValue: string;
  finalSellingPrice: string;
  roundingPolicy: string;
  projectedCost: string | null;
  costSource: string;
  branchId: string;
  pricingResolvedAt: string;
}

export interface FixedCartLine {
  cartLineId: string;
  lineType: "Fixed";
  itemId: string;
  itemNumber: string;
  itemName: string;
  quantity: string;
  customerId: string | null;
  priceTypeId: string;
  priceTypeCode: string;
  unitPrice: string;
  originalUnitPrice: string;
  priceSource: string;
  resolvedUnitPrice: string;
  resolvedPriceSource: string;
  priceOverrideMode: PriceOverrideMode;
  manualUnitPrice: string | null;
  manualLineTotal: string | null;
  lineTotal: string;
  pricingMode: "Fixed";
  pricingResolvedAt: string;
}

export interface VariableCartLine {
  cartLineId: string;
  lineType: "VariableValueByWeight";
  referenceItemId: string;
  referenceItemNumber: string;
  referenceItemName: string;
  stockOwnerItemId: string;
  stockOwnerItemNumber: string;
  enteredScaleValue: string;
  referenceBasePrice: string;
  referencePriceSource: string;
  weightKg: string;
  weightGrams: string;
  stockOwnerConversionFactor: string;
  projectedStockOwnerQuantity: string;
  customerId: string | null;
  variableValueDiscountPercent: string;
  rawDiscountedValue: string;
  finalSellingPrice: string;
  roundingPolicy: string;
  projectedCost: string | null;
  costSource: string;
  branchId: string;
  quantity: string;
  unitPrice: string;
  lineTotal: string;
  resolvedUnitPrice: string;
  resolvedPriceSource: string;
  priceOverrideMode: PriceOverrideMode;
  manualUnitPrice: string | null;
  manualLineTotal: string | null;
  pricingResolvedAt: string;
}

export type CartLine = FixedCartLine | VariableCartLine;

export interface CartDraft {
  cartId: string;
  status: "Active" | "NeedsReprice";
  scope: CartScope;
  customerId: string | null;
  lines: CartLine[];
  failedLineIds: string[];
  heldSaleId?: string | null;
  heldCartId?: string | null;
  heldRowVersion?: string | null;
}

export interface CartTotals {
  lineCount: number;
  fixedQuantity: string;
  total: string;
}

export function scopeKey(scope: CartScope): string {
  return [scope.businessId, scope.branchId, scope.stationId, scope.deviceId, scope.userId].join("|");
}

export function createClientId(): string {
  const source = globalThis.crypto;
  if (source && typeof source.randomUUID === "function") {
    try {
      return source.randomUUID();
    } catch {
      // http:// on a phone is not a secure context; randomUUID throws there.
    }
  }
  const bytes = new Uint8Array(16);
  if (source && typeof source.getRandomValues === "function") source.getRandomValues(bytes);
  else for (let index = 0; index < bytes.length; index += 1) bytes[index] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function createCart(scope: CartScope, cartId = createClientId()): CartDraft {
  return { cartId, status: "Active", scope, customerId: null, lines: [], failedLineIds: [] };
}

export function addFixedLine(cart: CartDraft, quote: FixedQuote, quantity = "1", lineId = createClientId()): CartDraft {
  if (!isPositiveDecimal(quantity)) {
    throw new Error("الكمية يجب أن تكون أكبر من صفر.");
  }

  const existing = cart.lines.find(
    (line): line is FixedCartLine =>
      line.lineType === "Fixed"
      && line.itemId === quote.itemId
      && line.priceTypeId === quote.priceTypeId
      && line.priceOverrideMode === "None"
      && decimalEquals(line.resolvedUnitPrice, quote.unitPrice)
      && line.customerId === cart.customerId,
  );
  if (existing) {
    return replaceLine(cart, changeFixedQuantity(existing, addDecimals(existing.quantity, quantity)));
  }

  const line: FixedCartLine = {
    cartLineId: lineId,
    lineType: "Fixed",
    itemId: quote.itemId,
    itemNumber: quote.itemNumber,
    itemName: quote.itemName,
    quantity,
    customerId: cart.customerId,
    priceTypeId: quote.priceTypeId,
    priceTypeCode: quote.priceTypeCode,
    unitPrice: quote.unitPrice,
    originalUnitPrice: quote.basePrice ?? quote.unitPrice,
    priceSource: quote.priceSource,
    resolvedUnitPrice: quote.unitPrice,
    resolvedPriceSource: quote.priceSource,
    priceOverrideMode: "None",
    manualUnitPrice: null,
    manualLineTotal: null,
    lineTotal: multiplyDecimals(quote.unitPrice, quantity),
    pricingMode: "Fixed",
    pricingResolvedAt: quote.pricingResolvedAt,
  };
  return { ...cart, status: "Active", failedLineIds: [], lines: [...cart.lines, line] };
}

export function addVariableLine(cart: CartDraft, quote: VariableQuote, lineId = createClientId()): CartDraft {
  const line: VariableCartLine = {
    cartLineId: lineId,
    lineType: "VariableValueByWeight",
    referenceItemId: quote.referenceItemId,
    referenceItemNumber: quote.referenceItemNumber,
    referenceItemName: quote.referenceItemName,
    stockOwnerItemId: quote.stockOwnerItemId,
    stockOwnerItemNumber: quote.stockOwnerItemNumber,
    enteredScaleValue: quote.enteredScaleValue,
    referenceBasePrice: quote.referenceBasePrice,
    referencePriceSource: quote.referencePriceSource,
    weightKg: quote.weightKg,
    weightGrams: quote.weightGrams,
    stockOwnerConversionFactor: quote.stockOwnerConversionFactor,
    projectedStockOwnerQuantity: quote.projectedStockOwnerQuantity,
    customerId: cart.customerId,
    variableValueDiscountPercent: quote.variableValueDiscountPercent,
    rawDiscountedValue: quote.rawDiscountedValue,
    finalSellingPrice: quote.finalSellingPrice,
    roundingPolicy: quote.roundingPolicy,
    projectedCost: quote.projectedCost,
    costSource: quote.costSource,
    branchId: quote.branchId,
    quantity: "1",
    unitPrice: quote.finalSellingPrice,
    lineTotal: quote.finalSellingPrice,
    resolvedUnitPrice: quote.finalSellingPrice,
    resolvedPriceSource: quote.referencePriceSource,
    priceOverrideMode: "None",
    manualUnitPrice: null,
    manualLineTotal: null,
    pricingResolvedAt: quote.pricingResolvedAt,
  };
  return { ...cart, status: "Active", failedLineIds: [], lines: [...cart.lines, line] };
}

export function variableLineTotals(line: VariableCartLine) {
  return {
    lineTotal: multiplyDecimals(line.finalSellingPrice, line.quantity),
    totalWeightGrams: multiplyDecimals(line.weightGrams, line.quantity),
    totalWeightKg: multiplyDecimals(line.weightKg, line.quantity),
    totalStockOwnerQuantity: multiplyDecimals(line.projectedStockOwnerQuantity, line.quantity),
  };
}

export function changeVariableQuantity(line: VariableCartLine, quantity: string): VariableCartLine {
  if (!isPositiveInteger(quantity)) {
    throw new Error("عدد الحصص يجب أن يكون عدداً صحيحاً أكبر من صفر.");
  }

  return commercialLine({ ...line, quantity });
}

export function updateVariableQuantity(cart: CartDraft, lineId: string, quantity: string): CartDraft {
  const line = cart.lines.find((item) => item.cartLineId === lineId);
  if (!line || line.lineType !== "VariableValueByWeight") {
    throw new Error("لا يمكن تعديل عدد حصص هذا السطر.");
  }

  return replaceLine(cart, changeVariableQuantity(line, quantity));
}

export function normalizeCartDraft(draft: CartDraft): CartDraft {
  return {
    ...draft,
    lines: draft.lines.map((line) => {
      const withOverride = withPriceDefaults(line);
      if (withOverride.lineType !== "VariableValueByWeight") {
        return commercialLine(withOverride);
      }
      const quantity = typeof withOverride.quantity === "string" && isPositiveInteger(withOverride.quantity) ? withOverride.quantity : "1";
      return changeVariableQuantity({ ...withOverride, quantity: "1" }, quantity);
    }),
  };
}

export function changeFixedQuantity(line: FixedCartLine, quantity: string): FixedCartLine {
  if (!isPositiveDecimal(quantity)) {
    throw new Error("الكمية يجب أن تكون أكبر من صفر.");
  }

  return commercialLine({ ...line, quantity });
}

export function updateFixedQuantity(cart: CartDraft, lineId: string, quantity: string): CartDraft {
  const line = cart.lines.find((item) => item.cartLineId === lineId);
  if (!line || line.lineType !== "Fixed") {
    throw new Error("لا يمكن تعديل كمية سطر الوزن.");
  }

  return replaceLine(cart, changeFixedQuantity(line, quantity));
}

export function removeLine(cart: CartDraft, lineId: string): CartDraft {
  return {
    ...cart,
    lines: cart.lines.filter((line) => line.cartLineId !== lineId),
    failedLineIds: cart.failedLineIds.filter((id) => id !== lineId),
  };
}

export function clearCart(cart: CartDraft): CartDraft {
  return { ...cart, status: "Active", customerId: null, lines: [], failedLineIds: [] };
}

export function cartTotals(cart: CartDraft): CartTotals {
  return {
    lineCount: cart.lines.length,
    fixedQuantity: cart.lines.reduce(
      (sum, line) => (line.lineType === "Fixed" ? addDecimals(sum, line.quantity) : sum),
      "0",
    ),
    total: cart.lines.reduce((sum, line) => addDecimals(sum, line.lineTotal), "0"),
  };
}

export function originalCartTotal(cart: CartDraft): string {
  return cart.lines.reduce((sum, line) => {
    const original = line.lineType === "Fixed" ? line.originalUnitPrice : line.enteredScaleValue;
    if (original == null || original === "") return sum;
    return addDecimals(sum, multiplyDecimals(original, line.quantity));
  }, "0");
}

export interface PricingGateway {
  resolveFixed(itemId: string, customerId: string | null, quantity?: string): Promise<FixedQuote>;
  resolveVariable(enteredScaleValue: string, customerId: string | null): Promise<VariableQuote>;
}

export async function backfillOriginalPrices(
  cart: CartDraft,
  resolveFixed: (line: FixedCartLine) => Promise<FixedQuote>,
): Promise<CartDraft> {
  let changed = false;
  const lines: CartLine[] = [];
  for (const line of cart.lines) {
    if (line.lineType !== "Fixed" || line.originalUnitPrice != null) {
      lines.push(line);
      continue;
    }
    try {
      const quote = await resolveFixed(line);
      if (quote.basePrice == null) {
        lines.push(line);
        continue;
      }
      changed = true;
      lines.push({ ...line, originalUnitPrice: quote.basePrice });
    } catch {
      lines.push(line);
    }
  }
  return changed ? { ...cart, lines } : cart;
}

export async function repriceCart(cart: CartDraft, customerId: string | null, gateway: PricingGateway): Promise<CartDraft> {
  const nextLines: CartLine[] = [];
  const failed: string[] = [];
  for (const line of cart.lines) {
    try {
      nextLines.push(line.lineType === "Fixed"
        ? repriceFixed(line, customerId, await gateway.resolveFixed(line.itemId, customerId, line.quantity))
        : repriceVariable(line, customerId, await gateway.resolveVariable(line.enteredScaleValue, customerId)));
    } catch {
      failed.push(line.cartLineId);
    }
  }

  if (failed.length > 0) {
    return { ...cart, status: "NeedsReprice", failedLineIds: failed };
  }

  return { ...cart, status: "Active", customerId, lines: nextLines, failedLineIds: [] };
}

function repriceFixed(line: FixedCartLine, customerId: string | null, quote: FixedQuote): FixedCartLine {
  return commercialLine({
    ...line,
    customerId,
    priceTypeId: quote.priceTypeId,
    priceTypeCode: quote.priceTypeCode,
    resolvedUnitPrice: quote.unitPrice,
    originalUnitPrice: quote.basePrice ?? line.originalUnitPrice,
    resolvedPriceSource: quote.priceSource,
    pricingResolvedAt: quote.pricingResolvedAt,
  });
}

function repriceVariable(line: VariableCartLine, customerId: string | null, quote: VariableQuote): VariableCartLine {
  return commercialLine({
    ...line,
    customerId,
    referenceBasePrice: quote.referenceBasePrice,
    referencePriceSource: quote.referencePriceSource,
    weightKg: quote.weightKg,
    weightGrams: quote.weightGrams,
    stockOwnerConversionFactor: quote.stockOwnerConversionFactor,
    projectedStockOwnerQuantity: quote.projectedStockOwnerQuantity,
    variableValueDiscountPercent: quote.variableValueDiscountPercent,
    rawDiscountedValue: quote.rawDiscountedValue,
    finalSellingPrice: quote.finalSellingPrice,
    roundingPolicy: quote.roundingPolicy,
    projectedCost: quote.projectedCost,
    costSource: quote.costSource,
    quantity: isPositiveInteger(line.quantity) ? line.quantity : "1",
    resolvedUnitPrice: quote.finalSellingPrice,
    resolvedPriceSource: quote.referencePriceSource,
    pricingResolvedAt: quote.pricingResolvedAt,
  });
}

function commercialLine<T extends CartLine>(line: T): T {
  const mode = line.priceOverrideMode ?? "None";
  if (mode === "UnitPrice" && line.manualUnitPrice) {
    return {
      ...line,
      priceOverrideMode: "UnitPrice",
      manualLineTotal: null,
      unitPrice: line.manualUnitPrice,
      priceSource: "ManualOverride",
      lineTotal: multiplyDecimals(line.manualUnitPrice, line.quantity),
    };
  }
  if (mode === "LineTotal" && line.manualLineTotal) {
    return {
      ...line,
      priceOverrideMode: "LineTotal",
      manualUnitPrice: null,
      unitPrice: roundDecimal(divideDecimals(line.manualLineTotal, line.quantity), 4),
      priceSource: "ManualOverride",
      lineTotal: line.manualLineTotal,
    };
  }
  const resolved = line.resolvedUnitPrice || (line.lineType === "Fixed" ? line.unitPrice : line.finalSellingPrice);
  const source = line.resolvedPriceSource || (line.lineType === "Fixed" ? line.priceSource : line.referencePriceSource);
  return {
    ...line,
    priceOverrideMode: "None",
    manualUnitPrice: null,
    manualLineTotal: null,
    unitPrice: resolved,
    priceSource: source,
    lineTotal: multiplyDecimals(resolved, line.quantity),
  };
}

function withPriceDefaults(line: CartLine): CartLine {
  if (line.priceOverrideMode) return line;
  if (line.lineType === "Fixed") {
    return { ...line, resolvedUnitPrice: line.unitPrice, resolvedPriceSource: line.priceSource, priceOverrideMode: "None", manualUnitPrice: null, manualLineTotal: null };
  }
  return { ...line, resolvedUnitPrice: line.finalSellingPrice, resolvedPriceSource: line.referencePriceSource, priceOverrideMode: "None", manualUnitPrice: null, manualLineTotal: null };
}

export function overrideUnitPrice(cart: CartDraft, lineId: string, unitPrice: string): CartDraft {
  const money = roundDecimal(unitPrice, 4);
  if (parseFails(money)) throw new Error("أدخل سعراً صحيحاً.");
  return replaceLine(cart, commercialLine(stamp(cart, lineId, { priceOverrideMode: "UnitPrice", manualUnitPrice: money, manualLineTotal: null })));
}

export function overrideLineTotal(cart: CartDraft, lineId: string, lineTotal: string): CartDraft {
  const money = roundDecimal(lineTotal, 4);
  if (parseFails(money)) throw new Error("أدخل إجمالاً صحيحاً.");
  return replaceLine(cart, commercialLine(stamp(cart, lineId, { priceOverrideMode: "LineTotal", manualLineTotal: money, manualUnitPrice: null })));
}

export function clearPriceOverride(cart: CartDraft, lineId: string): CartDraft {
  return replaceLine(cart, commercialLine(stamp(cart, lineId, { priceOverrideMode: "None", manualUnitPrice: null, manualLineTotal: null })));
}

function stamp(cart: CartDraft, lineId: string, patch: Partial<CartLine>): CartLine {
  const line = cart.lines.find((item) => item.cartLineId === lineId);
  if (!line) throw new Error("السطر غير موجود.");
  return { ...withPriceDefaults(line), ...patch } as CartLine;
}

function parseFails(value: string): boolean {
  return value.startsWith("-");
}

function replaceLine(cart: CartDraft, line: CartLine): CartDraft {
  return { ...cart, lines: cart.lines.map((item) => (item.cartLineId === line.cartLineId ? line : item)) };
}
