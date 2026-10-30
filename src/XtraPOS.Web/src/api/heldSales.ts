import { deviceHeaders } from "./client";
import type { DeviceSession } from "../types/setup";
import type { HeldLinePayload } from "../cart/heldInvoice";

export class HeldSaleClientError extends Error {
  readonly code: string;
  constructor(message: string, code = "") {
    super(message);
    this.name = "HeldSaleClientError";
    this.code = code;
  }
}

export interface HeldSaleSummary {
  saleId: string;
  saleNumber: number;
  createdAt: string;
  customerId: string | null;
  customerNumber: string | null;
  customerName: string | null;
  lineCount: number;
  total: string;
  updatedBy: string | null;
  notes: string | null;
  rowVersion: string;
}

export interface HeldSaleDetail extends HeldSaleSummary {
  cartId: string;
  status: string;
  lines: Array<Record<string, string | number | null>>;
}

export function createHeldSaleClient(session: DeviceSession) {
  return {
    save(body: { saleId: string | null; cartId: string; customerId: string | null; rowVersion: string | null; lines: HeldLinePayload[] }) {
      return send<HeldSaleDetail>("/api/pos/held-sales", "POST", body, session);
    },
    list(customer: string, from: string, to: string) {
      const query = new URLSearchParams();
      if (customer.trim()) query.set("customer", customer.trim());
      if (from) query.set("from", from);
      if (to) query.set("to", to);
      const suffix = query.size ? `?${query}` : "";
      return send<{ sales: HeldSaleSummary[] }>(`/api/pos/held-sales${suffix}`, "GET", undefined, session);
    },
    get(saleId: string) {
      return send<HeldSaleDetail>(`/api/pos/held-sales/${saleId}`, "GET", undefined, session);
    },
    release(saleId: string) {
      return send<{ released: boolean }>(`/api/pos/held-sales/${saleId}/release`, "POST", {}, session);
    },
  };
}

async function send<T>(path: string, method: string, body: unknown, session: DeviceSession): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      method,
      credentials: "include",
      headers: { "Content-Type": "application/json", ...deviceHeaders(session) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new HeldSaleClientError("تعذر الاتصال بالخادم. الفاتورة الحالية ما زالت على الشاشة.");
  }
  const payload = await response.json().catch(() => ({})) as { message?: string; code?: string };
  if (!response.ok) throw new HeldSaleClientError(payload.message || "تعذر تنفيذ عملية المسودة.", payload.code || "");
  return payload as T;
}
