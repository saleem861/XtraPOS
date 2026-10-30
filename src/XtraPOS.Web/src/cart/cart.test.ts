import { afterEach, describe, expect, it, vi } from "vitest";
import { createPricingClient } from "../api/pricing";
import { createIndexedDbDraftDatabase, createMemoryDraftDatabase, loadDraft, openCartDatabase, saveDraft } from "./draftStore";
import { formatPosMoney, multiplyDecimals } from "./decimal";
import {
  addFixedLine,
  addVariableLine,
  cartTotals,
  originalCartTotal,
  clearCart,
  createCart,
  removeLine,
  backfillOriginalPrices,
  repriceCart,
  scopeKey,
  clearPriceOverride,
  overrideLineTotal,
  overrideUnitPrice,
  updateFixedQuantity,
  updateVariableQuantity,
  variableLineTotals,
  type CartDraft,
  type CartScope,
  type FixedQuote,
  type PricingGateway,
  type VariableQuote,
} from "./model";
import type { DeviceSession } from "../types/setup";

const scope: CartScope = {
  businessId: "business",
  branchId: "branch",
  stationId: "station",
  deviceId: "device",
  userId: "cashier",
};

const fixed = (unitPrice = "10"): FixedQuote => ({
  itemId: "item-1",
  itemNumber: "100",
  itemName: "صنف ثابت",
  priceTypeId: "price-0",
  priceTypeCode: "Price0",
  basePrice: unitPrice,
  unitPrice,
  priceSource: "CentralDefault",
  pricingResolvedAt: "2026-10-22T00:00:00Z",
});

const weighed = (price = "20", weightKg = "0.1111111111111111111111111111"): VariableQuote => ({
  referenceItemId: "ref",
  referenceItemNumber: "1300",
  referenceItemName: "معسل فرنسي 1 كيلو",
  stockOwnerItemId: "owner",
  stockOwnerItemNumber: "1360",
  enteredScaleValue: "20",
  referenceBasePrice: "180",
  referencePriceSource: "CentralDefault",
  weightKg,
  weightGrams: "111.11111111111111111111111111",
  stockOwnerConversionFactor: "4",
  projectedStockOwnerQuantity: "0.4444444444444444444444444444",
  variableValueDiscountPercent: "0",
  rawDiscountedValue: price,
  finalSellingPrice: price,
  roundingPolicy: "LegacyVariableValueV1",
  projectedCost: null,
  costSource: "Unavailable",
  branchId: "branch",
  pricingResolvedAt: "2026-10-22T00:00:00Z",
});

function gateway(nextFixed = "8", nextVariable = "15", nextWeight = "0.1111111111111111111111111111"): PricingGateway {
  return {
    resolveFixed: async () => fixed(nextFixed),
    resolveVariable: async (entered) => ({ ...weighed(nextVariable, nextWeight), enteredScaleValue: entered }),
  };
}

describe("cashier money display", () => {
  it("shows at most two decimals and drops trailing zeros", () => {
    expect(formatPosMoney("25.0000")).toBe("25");
    expect(formatPosMoney("25.5000")).toBe("25.5");
    expect(formatPosMoney("25.2500")).toBe("25.25");
    expect(formatPosMoney("3.0000")).toBe("3");
    expect(formatPosMoney("3.1000")).toBe("3.1");
    expect(formatPosMoney("3.1200")).toBe("3.12");
    expect(formatPosMoney("25.2560")).toBe("25.26");
    expect(formatPosMoney("25.2540")).toBe("25.25");
    expect(formatPosMoney("0")).toBe("0");
  });
});

describe("pos cart draft", () => {
  it("keeps stable cart and line ids, backend prices, and exact totals", () => {
    const cart = createCart(scope, "cart-1");
    expect(cart.cartId).toBe("cart-1");
    expect(cart.customerId).toBeNull();
    expect(cartTotals(cart).total).toBe("0");
    expect(formatPosMoney(cartTotals(cart).total)).toBe("0");
    expect(originalCartTotal(cart)).toBe("0");

    const once = addFixedLine(cart, fixed(), "1", "line-fixed");
    const merged = addFixedLine(once, fixed(), "2");
    expect(merged.lines).toHaveLength(1);
    expect(merged.lines[0]?.cartLineId).toBe("line-fixed");
    expect(merged.lines[0]?.lineType === "Fixed" && merged.lines[0].quantity).toBe("3");
    expect(merged.lines[0]?.lineTotal).toBe(multiplyDecimals("10", "3"));

    const withWeight = addVariableLine(merged, weighed(), "weight-1");
    const stillSeparate = addVariableLine(withWeight, weighed(), "weight-2");
    expect(stillSeparate.lines.map((line) => line.cartLineId)).toEqual(["line-fixed", "weight-1", "weight-2"]);
    expect(stillSeparate.lines[1]?.lineType === "VariableValueByWeight" && stillSeparate.lines[1].quantity).toBe("1");
    expect(stillSeparate.lines[1]?.lineTotal).toBe("20");
    expect(cartTotals(stillSeparate)).toEqual({ lineCount: 3, fixedQuantity: "3", total: "70" });
  });

  it("sums quantity times the original price and ignores effective prices", () => {
    const priced = addFixedLine(createCart(scope), { ...fixed("9"), basePrice: "15" }, "2", "fixed");
    const weighted = addVariableLine(priced, { ...weighed("27"), enteredScaleValue: "30", referenceBasePrice: "80" }, "weight");
    expect(originalCartTotal(priced)).toBe("30");
    expect(originalCartTotal(weighted)).toBe("60");
    const manual = overrideLineTotal(overrideUnitPrice(weighted, "fixed", "8"), "fixed", "20");
    expect(originalCartTotal(manual)).toBe("60");
    expect(cartTotals(manual).total).not.toBe("60");
    const more = updateFixedQuantity(manual, "fixed", "3");
    expect(originalCartTotal(more)).toBe("75");
    expect(originalCartTotal(clearCart(more))).toBe("0");
    const restored = { ...more, lines: more.lines };
    expect(originalCartTotal(restored)).toBe(originalCartTotal(more));
  });

  it("reprices from the original inputs and preserves weight when only the discount changes", async () => {
    const cart = addVariableLine(addFixedLine(createCart(scope, "cart-1"), fixed(), "2", "line-fixed"), weighed(), "weight-1");
    const calls: string[] = [];
    const next = await repriceCart(cart, "customer-1", {
      resolveFixed: async (itemId, customerId) => {
        calls.push(`fixed:${itemId}:${customerId}`);
        return { ...fixed("8"), basePrice: "10" };
      },
      resolveVariable: async (entered, customerId) => {
        calls.push(`variable:${entered}:${customerId}`);
        return { ...weighed("15"), enteredScaleValue: entered, variableValueDiscountPercent: "22" };
      },
    });

    expect(calls).toEqual(["fixed:item-1:customer-1", "variable:20:customer-1"]);
    expect(next.cartId).toBe("cart-1");
    expect(next.customerId).toBe("customer-1");
    expect(next.lines.map((line) => line.cartLineId)).toEqual(["line-fixed", "weight-1"]);
    expect(next.lines[0]?.lineType === "Fixed" && next.lines[0].originalUnitPrice).toBe("10");
    expect(next.lines[0]?.lineType === "Fixed" && next.lines[0].unitPrice).toBe("8");
    expect(next.lines[0]?.lineType === "Fixed" && next.lines[0].lineTotal).toBe("16");
    expect(originalCartTotal(cart)).toBe("40");
    expect(originalCartTotal(next)).toBe("40");
    expect(next.lines[1]?.lineType === "VariableValueByWeight" && next.lines[1].enteredScaleValue).toBe("20");
    expect(next.lines[1]?.lineType === "VariableValueByWeight" && next.lines[1].weightKg).toBe("0.1111111111111111111111111111");
    expect(next.lines[1]?.lineTotal).toBe("15");
    expect(cartTotals(next).total).toBe("31");
  });

  it("marks NeedsReprice without mixing new prices when a line fails", async () => {
    const cart = addVariableLine(addFixedLine(createCart(scope, "cart-1"), fixed(), "1", "line-fixed"), weighed(), "weight-1");
    const failed = await repriceCart(cart, "customer-2", {
      resolveFixed: async () => fixed("8"),
      resolveVariable: async () => { throw new Error("offline"); },
    });

    expect(failed.status).toBe("NeedsReprice");
    expect(failed.failedLineIds).toEqual(["weight-1"]);
    expect(failed.customerId).toBeNull();
    expect(failed.lines).toEqual(cart.lines);
    expect(cartTotals(failed).total).toBe("30");
  });

  it("removes a line and clears without changing the cart id", () => {
    const cart = addFixedLine(createCart(scope, "cart-1"), fixed(), "1", "line-fixed");
    expect(removeLine(cart, "line-fixed").lines).toHaveLength(0);
    const cleared = clearCart(addVariableLine(cart, weighed(), "weight-1"));
    expect(cleared.cartId).toBe("cart-1");
    expect(cleared.lines).toHaveLength(0);
    expect(cleared.customerId).toBeNull();
  });

  it("backfills a missing original price without changing the saved unit price", async () => {
    const current = addFixedLine(createCart(scope), { ...fixed("8"), basePrice: "10" }, "1", "kept");
    const kept = current.lines[0];
    const legacy = {
      ...current,
      lines: [{ ...kept, originalUnitPrice: undefined, unitPrice: "8", priceOverrideMode: "None" as const }],
    };
    let calls = 0;
    const hydrated = await backfillOriginalPrices(legacy as CartDraft, async () => {
      calls += 1;
      return { ...fixed("8"), basePrice: "10" };
    });
    const line = hydrated.lines[0];
    if (line?.lineType !== "Fixed") throw new Error("missing");
    expect(calls).toBe(1);
    expect(line.originalUnitPrice).toBe("10");
    expect(line.unitPrice).toBe("8");

    const overridden = {
      ...current,
      lines: [{ ...kept, originalUnitPrice: undefined, unitPrice: "7", priceOverrideMode: "UnitPrice" as const, manualUnitPrice: "7" }],
    };
    const manual = await backfillOriginalPrices(overridden as CartDraft, async () => ({ ...fixed("8"), basePrice: "10" }));
    const manualLine = manual.lines[0];
    if (manualLine?.lineType !== "Fixed") throw new Error("missing");
    expect(manualLine.originalUnitPrice).toBe("10");
    expect(manualLine.unitPrice).toBe("7");

    const again = await backfillOriginalPrices(hydrated, async () => {
      calls += 1;
      return { ...fixed("8"), basePrice: "99" };
    });
    expect(calls).toBe(1);
    expect(again).toBe(hydrated);

    const zeroCart = {
      ...current,
      lines: [{ ...kept, originalUnitPrice: undefined, unitPrice: "8" }],
    };
    const zero = await backfillOriginalPrices(zeroCart as CartDraft, async () => ({ ...fixed("0"), basePrice: "0" }));
    const zeroLine = zero.lines[0];
    if (zeroLine?.lineType !== "Fixed") throw new Error("missing");
    expect(zeroLine.originalUnitPrice).toBe("0");
    expect(formatPosMoney(zeroLine.originalUnitPrice)).toBe("0");
    expect(zeroLine.unitPrice).toBe("8");
  });

  it("keeps the entered weighing value when the commercial price changes", () => {
    const quote = { ...weighed("30"), enteredScaleValue: "30", referenceBasePrice: "80" };
    const added = addVariableLine(createCart(scope), quote, "weight-1");
    const line = added.lines[0];
    if (line?.lineType !== "VariableValueByWeight") throw new Error("missing");
    expect(line.enteredScaleValue).toBe("30");
    expect(line.referenceBasePrice).toBe("80");
    expect(formatPosMoney(line.enteredScaleValue)).toBe("30");
    const overridden = overrideUnitPrice(added, "weight-1", "25");
    const next = overridden.lines[0];
    if (next?.lineType !== "VariableValueByWeight") throw new Error("missing");
    expect(next.enteredScaleValue).toBe("30");
    expect(next.unitPrice).toBe("25");
    expect(next.weightKg).toBe(line.weightKg);
    expect(formatPosMoney("0")).toBe("0");
  });

  it("keeps the price before customer pricing when the unit price changes", () => {
    const quote = { ...fixed("8"), basePrice: "10" };
    const added = addFixedLine(createCart(scope), quote, "1", "line-fixed");
    const line = added.lines[0];
    if (line?.lineType !== "Fixed") throw new Error("missing");
    expect(line.originalUnitPrice).toBe("10");
    expect(line.unitPrice).toBe("8");
    const overridden = overrideUnitPrice(added, "line-fixed", "7");
    const next = overridden.lines[0];
    if (next?.lineType !== "Fixed") throw new Error("missing");
    expect(next.originalUnitPrice).toBe("10");
    expect(next.unitPrice).toBe("7");
    const zero = addFixedLine(createCart(scope), { ...fixed("0"), basePrice: "0" }).lines[0];
    if (zero?.lineType !== "Fixed") throw new Error("missing");
    expect(zero.originalUnitPrice).toBe("0");
    expect(formatPosMoney(zero.originalUnitPrice)).toBe("0");
  });

  it("rejects a non-positive fixed quantity and refuses variable quantity edits", () => {
    const cart = addVariableLine(addFixedLine(createCart(scope), fixed(), "1", "line-fixed"), weighed(), "weight-1");
    expect(() => updateFixedQuantity(cart, "line-fixed", "0")).toThrow(/صفر/);
    expect(() => updateFixedQuantity(cart, "weight-1", "2")).toThrow(/الوزن/);
  });

  it("keeps a manual unit price and a manual total as the only authoritative input", () => {
    const cart = addFixedLine(createCart(scope), fixed("30"), "3", "line-fixed");
    const priced = overrideUnitPrice(cart, "line-fixed", "25.5");
    const unit = priced.lines[0];
    if (unit?.lineType !== "Fixed") throw new Error("missing");
    expect(unit.priceOverrideMode).toBe("UnitPrice");
    expect(unit.resolvedUnitPrice).toBe("30");
    expect(unit.originalUnitPrice).toBe("30");
    expect(unit.unitPrice).toBe("25.5");
    expect(unit.lineTotal).toBe("76.5");
    const more = updateFixedQuantity(priced, "line-fixed", "2");
    expect(more.lines[0]?.lineTotal).toBe("51");
    const totaled = overrideLineTotal(more, "line-fixed", "100");
    const totalLine = totaled.lines[0];
    if (totalLine?.lineType !== "Fixed") throw new Error("missing");
    expect(totalLine.priceOverrideMode).toBe("LineTotal");
    expect(totalLine.manualUnitPrice).toBeNull();
    expect(totalLine.lineTotal).toBe("100");
    expect(totalLine.unitPrice).toBe("50");
    const four = updateFixedQuantity(totaled, "line-fixed", "4");
    expect(four.lines[0]?.lineTotal).toBe("100");
    if (four.lines[0]?.lineType === "Fixed") expect(four.lines[0].unitPrice).toBe("25");
    const restored = clearPriceOverride(four, "line-fixed");
    expect(restored.lines[0]?.priceOverrideMode).toBe("None");
    expect(restored.lines[0]?.lineTotal).toBe("120");
  });

  it("counts variable portions without changing the scale snapshot or calling pricing", () => {
    const cart = addVariableLine(createCart(scope), weighed(), "weight-1");
    const line = cart.lines[0];
    if (line?.lineType !== "VariableValueByWeight") throw new Error("missing weight line");
    expect(line.quantity).toBe("1");
    const two = updateVariableQuantity(cart, "weight-1", "2");
    const three = updateVariableQuantity(two, "weight-1", "3");
    const weighedLine = three.lines[0];
    if (weighedLine?.lineType !== "VariableValueByWeight") throw new Error("missing weight line");
    expect(weighedLine.enteredScaleValue).toBe("20");
    expect(weighedLine.weightGrams).toBe("111.11111111111111111111111111");
    expect(weighedLine.projectedStockOwnerQuantity).toBe("0.4444444444444444444444444444");
    expect(variableLineTotals(weighedLine)).toMatchObject({
      lineTotal: "60",
      totalWeightGrams: multiplyDecimals("111.11111111111111111111111111", "3"),
      totalStockOwnerQuantity: multiplyDecimals("0.4444444444444444444444444444", "3"),
    });
    expect(weighedLine.lineTotal).toBe("60");
    const backToOne = updateVariableQuantity(three, "weight-1", "1");
    expect(backToOne.lines[0]?.lineType === "VariableValueByWeight" && backToOne.lines[0].quantity).toBe("1");
    expect(() => updateVariableQuantity(three, "weight-1", "0")).toThrow(/صفر/);
    expect(() => updateVariableQuantity(three, "weight-1", "1.5")).toThrow(/صفر/);
    expect(() => updateVariableQuantity(three, "weight-1", "-2")).toThrow(/صفر/);
    const fixedUpdated = updateFixedQuantity(addFixedLine(three, fixed(), "2", "line-fixed"), "line-fixed", "4");
    expect(fixedUpdated.lines[1]?.lineType === "Fixed" && fixedUpdated.lines[1].lineTotal).toBe("40");
    expect(cartTotals(three).total).toBe("60");
  });

  it("restores an old variable draft without quantity as one portion", async () => {
    const database = createMemoryDraftDatabase();
    const cart = addVariableLine(createCart(scope, "cart-old"), weighed(), "weight-old");
    const legacy = structuredClone(cart);
    delete (legacy.lines[0] as { quantity?: string }).quantity;
    await saveDraft(database, legacy);
    const restored = await loadDraft(database, scope);
    expect(restored?.lines[0]?.lineType === "VariableValueByWeight" && restored.lines[0].quantity).toBe("1");
    expect(restored?.lines[0]?.lineTotal).toBe("20");
    expect(restored?.lines[0]?.lineType === "VariableValueByWeight" && restored.lines[0].enteredScaleValue).toBe("20");
  });

  it("persists a draft and restores the same ids only for the same user and device", async () => {
    const database = createMemoryDraftDatabase();
    const cart = addFixedLine(createCart(scope, "cart-1"), fixed(), "1", "line-fixed");
    await saveDraft(database, cart);
    const restored = await loadDraft(database, scope);
    expect(restored?.cartId).toBe("cart-1");
    expect(restored?.lines[0]?.cartLineId).toBe("line-fixed");

    const otherUser = await loadDraft(database, { ...scope, userId: "other" });
    const otherDevice = await loadDraft(database, { ...scope, deviceId: "other-device" });
    expect(otherUser).toBeNull();
    expect(otherDevice).toBeNull();
    expect(scopeKey(scope)).not.toBe(scopeKey({ ...scope, userId: "other" }));
  });

  it("keeps the saved draft when repricing fails and does not duplicate lines after reconnect", async () => {
    const database = createMemoryDraftDatabase();
    const cart = addVariableLine(addFixedLine(createCart(scope, "cart-1"), fixed(), "1", "line-fixed"), weighed(), "weight-1");
    await saveDraft(database, cart);
    const visible = await loadDraft(database, scope);
    const failed = await repriceCart(visible!, "customer-1", {
      resolveFixed: async () => { throw new Error("branch api down"); },
      resolveVariable: async () => weighed(),
    });
    expect(failed.lines).toHaveLength(2);
    expect(failed.status).toBe("NeedsReprice");
    await saveDraft(database, failed);

    const repriced = await repriceCart(await loadDraft(database, scope) as typeof cart, null, gateway("10", "20"));
    expect(repriced.lines).toHaveLength(2);
    expect(repriced.lines.map((line) => line.cartLineId)).toEqual(["line-fixed", "weight-1"]);
    expect(repriced.status).toBe("Active");
  });
});

describe("indexedDB open races", () => {
  it("waits through onblocked and closes a second success", async () => {
    const closed: string[] = [];
    vi.stubGlobal("indexedDB", {
      open: () => {
        const database = {
          version: 1,
          objectStoreNames: ["cartDrafts"],
          close: () => closed.push("closed"),
          onversionchange: null as null | (() => void),
        };
        const request = {
          result: database,
          error: null,
          onsuccess: null as null | (() => void),
          onerror: null as null | (() => void),
          onblocked: null as null | (() => void),
          onupgradeneeded: null as null | (() => void),
        };
        queueMicrotask(() => {
          request.onblocked?.();
          request.onsuccess?.();
          request.onsuccess?.();
        });
        return request;
      },
    });
    const database = await openCartDatabase();
    expect(database.version).toBe(1);
    expect(closed).toEqual(["closed"]);
    vi.unstubAllGlobals();
  });
});

describe("indexedDB cart draft", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("round-trips the same cart through the IndexedDB adapter", async () => {
    installFakeIndexedDb();
    const database = createIndexedDbDraftDatabase();
    const cart = addFixedLine(createCart(scope, "cart-idb"), fixed(), "4", "line-idb");
    await saveDraft(database, cart);
    const restored = await loadDraft(database, scope);
    expect(restored).toEqual(cart);
    expect(await loadDraft(database, { ...scope, stationId: "other" })).toBeNull();
  });
});

describe("pricing client", () => {
  it("calls only the local pricing endpoints and keeps decimal text", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      urls.push(`${init?.method ?? "GET"} ${url} ${init?.body ?? ""}`);
      if (String(url).includes("variable-value")) {
        return new Response(JSON.stringify({
          referenceItemId: "ref",
          referenceItemNumber: "1300",
          stockOwnerItemId: "owner",
          stockOwnerItemNumber: "1360",
          enteredScaleValue: 20,
          referenceBasePrice: 180,
          referencePriceSource: "CentralDefault",
          weightKg: 0.1111111111111111111111111111,
          weightGrams: 111.11,
          stockOwnerConversionFactor: 4,
          projectedStockOwnerQuantity: 0.4444,
          variableValueDiscountPercent: 0,
          rawDiscountedValue: 20,
          finalSellingPrice: 20,
          roundingPolicy: "LegacyVariableValueV1",
          projectedCost: null,
          costSource: "Unavailable",
          branchId: "branch",
        }));
      }
      return new Response(JSON.stringify({
        itemId: "item-1",
        priceTypeId: "price-0",
        priceTypeCode: "Price0",
        resolvedPrice: 15,
        priceSource: "CentralDefault",
      }));
    }));
    const client = createPricingClient({ deviceId: "device", deviceToken: "token" } as DeviceSession);
    const quote = await client.resolveFixed("item-1", null, "100", "صنف");
    const variable = await client.resolveVariable("20", null, "معسل");
    expect(quote.unitPrice).toBe("15");
    expect(quote.basePrice).toBe("15");
    expect(variable.enteredScaleValue).toBe("20");
    expect(urls.some((url) => url.includes("/api/stock") || url.includes("StockMovement"))).toBe(false);
    expect(urls[0]).toContain("/api/pricing/items/item-1/resolve");
    expect(urls[1]).toContain("POST /api/pricing/variable-value/resolve");
    vi.unstubAllGlobals();
  });
});

function installFakeIndexedDb() {
  const rows = new Map<string, unknown>();
  const database = {
    created: false,
    objectStoreNames: { contains: () => database.created },
    createObjectStore() { this.created = true; },
    transaction() {
      return {
        objectStore: () => ({
          get: (key: string) => complete(rows.get(key)),
          put: (value: { scopeKey: string }) => { rows.set(value.scopeKey, value); return complete(undefined); },
          delete: (key: string) => { rows.delete(key); return complete(undefined); },
        }),
      };
    },
    close() {},
  };
  vi.stubGlobal("indexedDB", {
    open: () => {
      const request = complete(database, false);
      queueMicrotask(() => {
        if (!database.created) request.onupgradeneeded?.();
        request.onsuccess?.();
      });
      return request;
    },
  });
}

function complete(result: unknown, auto = true) {
  const request = {
    result,
    error: null,
    onsuccess: null as null | (() => void),
    onerror: null as null | (() => void),
    onupgradeneeded: null as null | (() => void),
  };
  if (auto) queueMicrotask(() => request.onsuccess?.());
  return request;
}
