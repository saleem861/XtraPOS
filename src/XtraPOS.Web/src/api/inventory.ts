import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";
import { BACKGROUND_HEADER } from "../components/waitingActivity";

export type InventoryType = "Important" | "Periodic" | "Manual" | "Custom";

export interface InventoryConfiguration {
  businessDate: string;
  periodType: "AM" | "PM";
  cutoff: string;
  pageCount: number;
  defaultPage: number | null;
  importantItems: number;
  pageItems: { page: number; items: number }[];
  /** الجرد المخصص لهذا الفرع كما نزل من المركز («تحديد جرد»)، وحالته في كل فرع مخصص له (كما في النظام القديم). */
  customInventories?: { customInventoryId: string; name: string; items: number; branches?: { branchId?: string; branchName: string; completed: boolean }[] }[];
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
    throw Object.assign(new SetupClientError(message), { status: response.status });
  }
  return (await response.json()) as T;
}

/** رمز الاستجابة لخطأ من الخادم (4xx خطأ في الطلب لا يُعاد، 5xx أو انقطاع يُعاد). */
export function errorStatus(error: unknown): number | undefined {
  return typeof (error as { status?: unknown })?.status === "number" ? (error as { status: number }).status : undefined;
}

export interface InventoryBranch {
  branchId: string;
  branchName: string;
  current: boolean;
  lastPushAt: string | null;
  stale: boolean;
}

/**
 * عميل الجرد. بلا branchId (أو بفرع الجهاز) يعمل على قاعدة الفرع المحلية كما كان.
 * بفرع آخر (ملاحظة المالك: لا يُقيَّد الجرد بالفرع الحالي) يجري الجرد على المركز، والتثبيت يرسل طلبات تصحيح بالفارق.
 */
export function createInventoryClient(session: DeviceSession, branchId = "") {
  const headers = deviceHeaders(session);
  const json = { ...headers, "Content-Type": "application/json" };
  const remote = branchId && branchId !== session.branchId ? branchId : "";
  const query = (params?: URLSearchParams) => {
    const all = params ?? new URLSearchParams();
    if (remote) all.set("branchId", remote);
    const text = all.toString();
    return text ? `?${text}` : "";
  };
  return {
    remote: Boolean(remote),
    async branches() {
      const response = await fetch("/api/inventory/branches", { headers });
      return readJson<{ branches: InventoryBranch[]; notice: string | null; central?: boolean }>(response, "تعذر تحميل الفروع.");
    },
    async configuration() {
      const response = await fetch(`/api/inventory/configuration${query()}`, { headers });
      return readJson<InventoryConfiguration>(response, "تعذر تحميل إعدادات الجرد.");
    },
    async findOpen(type: InventoryType, pages: number[], customInventoryId = "") {
      const params = new URLSearchParams({ type, pages: pages.join(",") });
      if (customInventoryId) params.set("customInventoryId", customInventoryId);
      const response = await fetch(`/api/inventory/sessions/open${query(params)}`, { headers });
      return readJson<{ session: InventorySession | null }>(response, "تعذر التحقق من وجود جرد مفتوح.");
    },
    async start(type: InventoryType, pages: number[], replaceOpen: boolean, customInventoryId = "") {
      const response = await fetch("/api/inventory/sessions", {
        method: "POST",
        headers: json,
        body: JSON.stringify({ inventoryType: type, pages, replaceOpen, customInventoryId: customInventoryId || null, ...(remote ? { branchId: remote } : {}) }),
      });
      return readJson<{ session: InventorySession; resumed: boolean }>(response, "تعذر بدء الجرد حالياً. حاول مرة أخرى.");
    },
    async items(sessionId: string) {
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/items${query()}`, { headers });
      return readJson<{ session: InventorySession; items: InventoryItem[] }>(response, "تعذر تحميل أصناف الجرد. حاول مرة أخرى.");
    },
    async lookup(sessionId: string, code: string) {
      const params = new URLSearchParams({ code });
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/lookup${query(params)}`, { headers });
      return readJson<InventoryLookup>(response, "الصنف غير موجود.");
    },
    /** ملاحظة المالك: الحفظ في الخلفية، فلا يظهر «يرجى الانتظار» لكل صنف. */
    async count(sessionId: string, itemId: string, quantity: number, background = false) {
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/count`, {
        method: "PUT",
        headers: background ? { ...json, [BACKGROUND_HEADER]: "1" } : json,
        body: JSON.stringify({ itemId, quantity, ...(remote ? { branchId: remote } : {}) }),
      });
      return readJson<{ session: InventorySession; item: InventoryItem | null }>(response, "تعذر حفظ العد. حاول مرة أخرى.");
    },
    /**
     * ملاحظة المالك (25 نوفمبر): حفظ كل العدّ المنتظر في طلب واحد، في الخلفية، بمهلة 20 ثانية.
     * المهلة تُرجع خطأً بلا رمز، فيُعاد الطلب تلقائياً ولا يتعلق الطابور.
     */
    async countBatch(sessionId: string, items: { itemId: string; quantity: number }[], timeoutMs = 20000) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      try {
        const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/counts`, {
          method: "PUT",
          headers: { ...json, [BACKGROUND_HEADER]: "1" },
          body: JSON.stringify({ items, ...(remote ? { branchId: remote } : {}) }),
          signal: controller.signal,
        });
        return await readJson<{
          session: InventorySession;
          items: InventoryItem[];
          rejected: { itemId: string; message: string }[];
        }>(response, "تعذر حفظ العد. حاول مرة أخرى.");
      } catch (error) {
        if ((error as Error).name === "AbortError") throw new Error("لم يرد المركز خلال 20 ثانية.");
        throw error;
      } finally {
        clearTimeout(timer);
      }
    },
    async finalize(sessionId: string, counts: { itemId: string; quantity: number }[] = []) {
      const response = await fetch(`/api/inventory/sessions/${encodeURIComponent(sessionId)}/finalize${query()}`, {
        method: "POST",
        headers: json,
        body: JSON.stringify({ items: counts, ...(remote ? { branchId: remote } : {}) }),
      });
      return readJson<{ session: InventorySession; posted: number; remote?: boolean; queued?: boolean }>(response, "تعذر تثبيت الجرد. حاول مرة أخرى.");
    },
  };
}

export type InventoryClient = ReturnType<typeof createInventoryClient>;
