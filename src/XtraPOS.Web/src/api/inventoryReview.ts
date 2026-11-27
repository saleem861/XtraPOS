import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";
import type { InventoryType } from "./inventory";

export type AuditMode = "All" | "Differences" | "Manual";

export interface ReportSession {
  sessionId: string;
  inventoryType: InventoryType;
  inventoryTypeLabel: string;
  status: "Open" | "Completed" | "Discarded";
  userId: string;
  userName: string | null;
  businessDate: string;
  startedAt: string;
  lastSavedAt: string;
  completedAt: string | null;
  totalItems: number;
  countedItems: number;
  postedItems: number;
  completionPercent: number;
  differences: number;
  audits: number;
  pages: number[];
}

export interface ReportBranch {
  branchId: string;
  branchName: string;
  current: boolean;
  lastPushAt: string | null;
  stale: boolean;
}

export interface InventoryReport {
  branchId: string;
  remote: boolean;
  from: string;
  to: string;
  sessions: ReportSession[];
  employees: { userId: string; userName: string | null }[];
}

export interface ReportFilter {
  /** فارغ = فرع الجهاز. فرع آخر يُقرأ من نسخته على المركز. */
  branchId: string;
  from: string;
  to: string;
  type: InventoryType | "";
  userId: string;
  status: "" | "Open" | "Finalizing" | "Completed" | "Discarded";
}

export interface ReportLine {
  sessionItemId: string;
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  isCounted: boolean;
  countedQuantity: number | null;
  expectedQuantity: number | null;
  difference: number | null;
  countedAt: string | null;
  countedByName: string | null;
  posted: boolean;
  quantityAfterPosting: number | null;
}

export interface AuditSummary {
  auditId: string;
  userName: string | null;
  auditMode: AuditMode;
  auditModeLabel: string;
  status: "Open" | "Completed" | "Discarded";
  startedAt: string;
  completedAt: string | null;
  totalItems: number;
  countedItems: number;
  matchedItems: number;
}

export interface SessionDetail {
  session: {
    sessionId: string;
    inventoryType: InventoryType;
    inventoryTypeLabel: string;
    status: "Open" | "Completed" | "Discarded";
    userName: string | null;
    businessDate: string;
    startedAt: string;
    completedAt: string | null;
    totalItems: number;
    countedItems: number;
    postedItems: number;
  };
  items: ReportLine[];
  audits: AuditSummary[];
  /** جرد فرع آخر من نسخته على المركز: للقراءة فقط. */
  remote: boolean;
}

export interface AuditLine {
  auditItemId: string;
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  isCounted: boolean;
  auditQuantity: number | null;
  countedAt: string | null;
  /** تظهر بعد إنهاء التدقيق فقط: التدقيق أعمى. */
  employeeQuantity: number | null;
  employeeName: string | null;
  systemQuantity: number | null;
  difference: number | null;
  matched: boolean | null;
}

export interface AuditView {
  audit: AuditSummary & {
    sessionId: string;
    mine: boolean;
    inventoryTypeLabel: string;
    employeeName: string | null;
    businessDate: string;
  };
  lines: AuditLine[];
}

export interface ItemCorrection {
  previousQuantity: number | null;
  newQuantity: number;
  expectedQuantity?: number | null;
  countedQuantity?: number | null;
  deltaQuantity?: number | null;
  reason: string;
  userName: string | null;
  correctedAt: string;
}

export interface ItemProcessing {
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  scannedItemNumber: string | null;
  convertedFromPackage: boolean;
  isImportant: boolean;
  inventoryPage: number | null;
  supplyDays: number | null;
  classifiedAt: string | null;
  classifiedBy: string | null;
  onHandQuantity: number | null;
  quantityKnown: boolean;
  dailySalesRate: number | null;
  corrections: ItemCorrection[];
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { message?: string };
      if (body.message?.trim()) message = body.message;
    } catch {
      message = response.status === 403 ? "ليست لديك صلاحية لهذه الشاشة." : fallback;
    }
    throw new SetupClientError(message);
  }
  return (await response.json()) as T;
}

export function newRequestId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") return crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (char) => {
    const random = (Math.random() * 16) | 0;
    return (char === "x" ? random : (random & 0x3) | 0x8).toString(16);
  });
}

export function createInventoryReviewClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  const json = { ...headers, "Content-Type": "application/json" };
  return {
    async branches() {
      const response = await fetch("/api/inventory/report/branches", { headers });
      return readJson<{ centralReachable: boolean; branches: ReportBranch[] }>(response, "تعذر تحميل الفروع.");
    },
    async report(filter: ReportFilter) {
      const params = new URLSearchParams();
      if (filter.branchId) params.set("branchId", filter.branchId);
      if (filter.from) params.set("from", filter.from);
      if (filter.to) params.set("to", filter.to);
      if (filter.type) params.set("type", filter.type);
      if (filter.userId) params.set("userId", filter.userId);
      if (filter.status) params.set("status", filter.status);
      const response = await fetch(`/api/inventory/report/sessions?${params}`, { headers });
      return readJson<InventoryReport>(response, "تعذر تحميل تقرير الجرد.");
    },
    async session(sessionId: string, branchId = "") {
      const query = branchId ? `?${new URLSearchParams({ branchId })}` : "";
      const response = await fetch(`/api/inventory/report/sessions/${encodeURIComponent(sessionId)}${query}`, { headers });
      return readJson<SessionDetail>(response, "تعذر تحميل تفاصيل الجرد.");
    },
    async startAudit(sessionId: string, mode: AuditMode, sessionItemIds: string[]) {
      const response = await fetch(`/api/inventory/report/sessions/${encodeURIComponent(sessionId)}/audits`, {
        method: "POST",
        headers: json,
        body: JSON.stringify({ mode, sessionItemIds }),
      });
      return readJson<AuditView>(response, "تعذر بدء التدقيق.");
    },
    async audit(auditId: string) {
      const response = await fetch(`/api/inventory/audits/${encodeURIComponent(auditId)}`, { headers });
      return readJson<AuditView>(response, "تعذر تحميل التدقيق.");
    },
    async auditCount(auditId: string, auditItemId: string, quantity: number) {
      const response = await fetch(`/api/inventory/audits/${encodeURIComponent(auditId)}/count`, {
        method: "PUT",
        headers: json,
        body: JSON.stringify({ auditItemId, quantity }),
      });
      return readJson<AuditView>(response, "تعذر حفظ عدّ التدقيق.");
    },
    async completeAudit(auditId: string) {
      const response = await fetch(`/api/inventory/audits/${encodeURIComponent(auditId)}/complete`, { method: "POST", headers: json });
      return readJson<AuditView>(response, "تعذر إنهاء التدقيق.");
    },
    async item(code: string) {
      const params = new URLSearchParams({ code });
      const response = await fetch(`/api/inventory/items/lookup?${params}`, { headers });
      return readJson<ItemProcessing>(response, "الصنف غير موجود.");
    },
    async classify(itemId: string, isImportant: boolean, inventoryPage: number | null, supplyDays: number | null) {
      const response = await fetch(`/api/inventory/items/${encodeURIComponent(itemId)}/classification`, {
        method: "PUT",
        headers: json,
        body: JSON.stringify({ isImportant, inventoryPage, supplyDays }),
      });
      return readJson<ItemProcessing>(response, "تعذر حفظ التصنيف.");
    },
    async correctQuantity(itemId: string, correctionId: string, quantity: number, reason: string) {
      const response = await fetch(`/api/inventory/items/${encodeURIComponent(itemId)}/quantity`, {
        method: "POST",
        headers: json,
        body: JSON.stringify({ correctionId, quantity, reason }),
      });
      return readJson<ItemProcessing>(response, "تعذر تصحيح الكمية. لم يتغير الرصيد.");
    },
  };
}

export type InventoryReviewClient = ReturnType<typeof createInventoryReviewClient>;
