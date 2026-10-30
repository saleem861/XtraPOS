import { deviceHeaders } from "./client";
import { isPositiveDecimal } from "../cart/decimal";
import type { FixedQuote, VariableQuote } from "../cart/model";
import type { DeviceSession } from "../types/setup";

export class PricingClientError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "PricingClientError";
  }
}

interface FixedPriceResponse {
  itemId: string;
  priceTypeId: string;
  priceTypeCode: string;
  basePrice: string;
  resolvedPrice: string;
  priceSource: string;
}

interface VariablePriceResponse {
  referenceItemId: string;
  referenceItemNumber: string;
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
}

function quoteNumbers(json: string): string {
  return json.replace(/-?\d+(?:\.\d+)?(?=\s*[,}\]])/g, (value) => `"${value}"`);
}

async function readPricing<T>(response: Response, fallback: string): Promise<T> {
  const text = await response.text();
  const body = text ? JSON.parse(quoteNumbers(text)) as { message?: string } : {};
  if (!response.ok) {
    throw new PricingClientError(body.message || fallback);
  }

  return body as T;
}

export function createPricingClient(session: DeviceSession) {
  return {
    async resolveFixed(itemId: string, customerId: string | null, itemNumber: string, itemName: string, quantity = "1", applySpecial = true): Promise<FixedQuote> {
      const params = new URLSearchParams();
      if (customerId) params.set("customerId", customerId);
      if (quantity && quantity !== "1") params.set("quantity", quantity);
      if (!applySpecial) params.set("applySpecial", "false");
      const query = params.size ? `?${params.toString()}` : "";
      const response = await fetch(`/api/pricing/items/${itemId}/resolve${query}`, { headers: deviceHeaders(session) });
      const price = await readPricing<FixedPriceResponse>(response, "تعذر تسعير الصنف.");
      return {
        itemId: price.itemId,
        itemNumber,
        itemName,
        priceTypeId: price.priceTypeId,
        priceTypeCode: price.priceTypeCode,
        basePrice: price.basePrice ?? price.resolvedPrice,
        unitPrice: price.resolvedPrice,
        priceSource: price.priceSource,
        pricingResolvedAt: new Date().toISOString(),
      };
    },
    async resolveVariable(enteredScaleValue: string, customerId: string | null, referenceItemName: string): Promise<VariableQuote> {
      if (!isPositiveDecimal(enteredScaleValue)) {
        throw new PricingClientError("قيمة الميزان غير صالحة.");
      }
      if (customerId && !/^[0-9a-fA-F-]{36}$/.test(customerId)) {
        throw new PricingClientError("الزبون غير صالح.");
      }
      const response = await fetch("/api/pricing/variable-value/resolve", {
        method: "POST",
        headers: { "Content-Type": "application/json", ...deviceHeaders(session) },
        body: `{"enteredScaleValue":${enteredScaleValue},"customerId":${customerId ? `"${customerId}"` : "null"}}`,
      });
      const price = await readPricing<VariablePriceResponse>(response, "تعذر تسعير الوزن.");
      return {
        ...price,
        referenceItemName,
        pricingResolvedAt: new Date().toISOString(),
      };
    },
  };
}
