import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export type InventoryType = "Important" | "Periodic" | "Manual" | "Custom";

export interface InventoryConfiguration {
  businessDate: string;
  periodType: "AM" | "PM";
  cutoff: string;
  pageCount: number;
  defaultPage: number | null;
  importantItems: number;
  pageItems: { page: number; items: number }[];
  /** الجرد المخصص لهذا الفرع كما نزل من المركز («تحديد جرد»). */
  customInventories?: { customInventoryId: string; name: string; items: number }[];
}

export interface InventorySession {
  sessionId: string;
  inventoryType: InventoryType;
  inventoryTypeLabel: string;
  status: "Open" | "Completed" | "Discarded";
  businessDate: string;
  startedAt: string;
  lastSavedAt: string;
  completedAt: string | null;
  totalItems: number;
  countedItems: number;
  uncountedItems: number;
  postedItems: number;
  pages: number[];
}

export interface InventoryItem {
  sessionItemId: string;
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  isCounted: boolean;
  countedQuantity: number | null;
  countedAt: string | null;
  countedByName: string | null;
  posted: boolean;
  /** يظهر فقط لمن يملك صلاحية مراجعة الجرد. */
  expectedQuantity: number | null;
  difference: number | null;
  quantityAfterPosting: number | null;
}

export interface InventoryLookup {
  itemId: string;
  itemNumber: string;
  itemName: string;
  scannedItemNumber: string;
  convertedFromPackage: boolean;
  inSession: boolean;
  isCounted: boolean;
  countedQuantity: number | null;
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { message?: string };
      if (body.message?.trim()) message = body.message;
    } catch {
      message = response.status === 403 ? "ليست لديك صلاحية الجرد." : fallback;
    }
    throw new SetupClientError(message);
  }
  return (await response.json()) as T;
}

export function createInventoryClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  const json = { ...headers, "Content-Type": "application/json" };
  return {
    async configuration() {
      const response = await fetch("/api/inventory/configuration", { headers });
      return readJson<InventoryConfiguration>(response, "تعذر تحميل إعدادات الجرد.");
    },
    async findOpen(type: InventoryType, pages: number[], customInventoryId = "") {
      const params = new URLSearchParams({ type, pages: pages.join(",") });
      if (customInventoryId) params.set("customInventoryId", customInventoryId);
      const response = await fetch(`/api/inventory/sessions/open?${params}`, { headers });
      return readJson<{ session: InventorySession | null }>(response, "تعذر التحقق من وجود جرد مفتوح.");
    },
    async start(type: InventoryType, pages: number[], replaceOpen: boolean, customInventoryId = "") {
      const response = await fetch("/api/inventory/sessions", {
        method: "POST",
        headers: json,
        body: JSON.stringify({ inventoryType: type, pages, replaceOpen, customInventoryId: customInventoryId || null }),
      });
      return readJson<{ session: InventorySession; resumed: boolean }>(response, "تعذر بدء الجرد حالياً. حاول مرة أخرى.");
    },
    async items(sessionId: string) {
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/items`, { headers });
      return readJson<{ session: InventorySession; items: InventoryItem[] }>(response, "تعذر تحميل أصناف الجرد. حاول مرة أخرى.");
    },
    async lookup(sessionId: string, code: string) {
      const params = new URLSearchParams({ code });
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/lookup?${params}`, { headers });
      return readJson<InventoryLookup>(response, "الصنف غير موجود.");
    },
    async count(sessionId: string, itemId: string, quantity: number) {
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/count`, {
        method: "PUT",
        headers: json,
        body: JSON.stringify({ itemId, quantity }),
      });
      return readJson<{ session: InventorySession; item: InventoryItem | null }>(response, "تعذر حفظ العد. حاول مرة أخرى.");
    },
    async finalize(sessionId: string) {
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/finalize`, {
        method: "POST",
        headers: json,
      });
      return readJson<{ session: InventorySession; posted: number }>(response, "تعذر تثبيت الجرد. حاول مرة أخرى.");
    },
  };
}

export type InventoryClient = ReturnType<typeof createInventoryClient>;
