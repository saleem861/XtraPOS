import { deviceHeaders } from "./client";
import type { CartDraft } from "../cart/model";
import type { DeviceSession } from "../types/setup";

export class CheckoutClientError extends Error {
  readonly code: string;
  constructor(message: string, code = "") {
    super(message);
    this.name = "CheckoutClientError";
    this.code = code;
  }
}

export interface CompletedSale {
  saleId: string;
  saleNumber: number;
  total: string;
  payments: { method: string; amount: string; tenderedAmount: string | null; changeAmount: string }[];
  idempotentReplay: boolean;
}

export function createCheckoutClient(session: DeviceSession) {
  return {
    async checkout(cart: CartDraft, payments: { method: string; amount: string; tenderedAmount: string | null; clientPaymentKey: string }[]) {
      const lines = cart.lines.map((line) => {
        const manual = line.priceOverrideMode === "UnitPrice"
          ? `,"priceOverrideMode":"UnitPrice","manualUnitPrice":${line.manualUnitPrice}`
          : line.priceOverrideMode === "LineTotal"
            ? `,"priceOverrideMode":"LineTotal","manualLineTotal":${line.manualLineTotal}`
            : `,"priceOverrideMode":"None"`;
        return line.lineType === "Fixed"
          ? `{"cartLineId":"${line.cartLineId}","lineType":"Fixed","itemId":"${line.itemId}","quantity":${line.quantity},"expectedUnitPrice":${line.resolvedUnitPrice}${manual}}`
          : `{"cartLineId":"${line.cartLineId}","lineType":"VariableValueByWeight","enteredScaleValue":${line.enteredScaleValue},"quantity":${line.quantity},"expectedFinalSellingPrice":${line.finalSellingPrice},"expectedReferenceBasePrice":${line.referenceBasePrice},"expectedWeightKg":${line.weightKg},"expectedStockOwnerQuantity":${line.projectedStockOwnerQuantity}${manual}}`;
      });
      const paymentJson = payments.map((payment) =>
        `{"clientPaymentKey":"${payment.clientPaymentKey}","method":"${payment.method}","amount":${payment.amount},"tenderedAmount":${payment.tenderedAmount ?? "null"}}`);
      let response: Response;
      try {
        response = await fetch("/api/pos/checkout", {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json", ...deviceHeaders(session) },
          body: `{"cartId":"${cart.cartId}","customerId":${cart.customerId ? `"${cart.customerId}"` : "null"},"lines":[${lines.join(",")}],"payments":[${paymentJson.join(",")}]}`,
        });
      } catch {
        throw new CheckoutClientError("خادم الفرع غير متاح. السلة محفوظة على هذا الجهاز.");
      }
      const body = await response.json().catch(() => ({})) as {
        code?: string;
        message?: string;
        saleId?: string;
        saleNumber?: number;
        total?: number | string;
        payments?: { method: string; amount: number | string; tenderedAmount: number | string | null; changeAmount: number | string }[];
        idempotentReplay?: boolean;
      };
      if (!response.ok) {
        throw new CheckoutClientError(body.message || "تعذر إتمام البيع.", body.code || "");
      }
      return {
        saleId: body.saleId || "",
        saleNumber: body.saleNumber || 0,
        total: String(body.total ?? ""),
        payments: (body.payments || []).map((payment) => ({
          method: payment.method,
          amount: String(payment.amount),
          tenderedAmount: payment.tenderedAmount == null ? null : String(payment.tenderedAmount),
          changeAmount: String(payment.changeAmount),
        })),
        idempotentReplay: Boolean(body.idempotentReplay),
      } satisfies CompletedSale;
    },
  };
}
