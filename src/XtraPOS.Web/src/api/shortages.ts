import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";
import { BACKGROUND_HEADER } from "../components/waitingActivity";
import type { InventoryType } from "./inventory";

export interface ShortageBranchOption {
  branchId: string;
  branchName: string;
  current: boolean;
  lastPushAt: string | null;
  stale: boolean;
}

export interface UserOption {
  userId: string;
  displayName: string;
  employeeNumber: string | null;
}

export interface RunHeader {
  runId: string;
  shortageType: InventoryType;
  shortageTypeLabel: string;
  supplyDays: number;
  pages: number[];
  status: "Draft" | "Completed" | "Discarded";
  createdByName: string | null;
  createdAt: string;
  lastSavedAt: string;
  completedAt: string | null;
  itemCount: number;
  processedCount: number;
  mine: boolean;
  customInventoryName: string | null;
  manualFrom: string | null;
  manualTo: string | null;
  branches: { branchId: string; branchName: string | null; snapshotAt: string | null; stale: boolean }[];
}

export interface ShortageOptions {
  branches: ShortageBranchOption[];
  customInventories: { customInventoryId: string; name: string; items: number }[];
  users: UserOption[];
  draft: RunHeader | null;
  pageCount: number;
  /** صفحات جدول اليوم (كما في النظام القديم: محددة مسبقاً ويمكن تغييرها). */
  todayPages?: number[];
}

export interface StartShortageRequest {
  shortageType: InventoryType;
  branchIds: string[];
  pages: number[];
  supplyDays: number;
  customInventoryId: string | null;
  manualFrom: string | null;
  manualTo: string | null;
  userIds: string[];
  replaceDraft: boolean;
}

export interface RunItemBranch {
  branchId: string;
  branchName: string | null;
  currentQuantity: number | null;
  dailySalesRate: number | null;
  target: number;
  need: number;
  surplus: number;
}

export interface RunItem {
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  totalNeed: number;
  totalSurplus: number;
  requiredQuantity: number;
  balanceQuantity: number;
  missingRate: boolean;
  missingQuantity: boolean;
  isProcessed: boolean;
  purchaseQuantity: number | null;
  warehouseQuantity: number | null;
  balanceDecision: number | null;
  deleteRequested: boolean;
  alreadySupplied: boolean;
  note: string | null;
  processedByName: string | null;
  substituteItemNumber?: string | null;
  branches: RunItemBranch[];
}

export interface ShortageDecisionBody {
  purchaseQuantity: number | null;
  warehouseQuantity: number | null;
  balanceQuantity: number | null;
  delete: boolean;
  alreadySupplied: boolean;
  note: string | null;
  /** رقم الصنف البديل، مع الحذف فقط. */
  substituteItemNumber?: string | null;
  clear?: boolean;
}

export interface ItemBranchState {
  branchId: string;
  branchName: string;
  current: boolean;
  quantity: number | null;
  dailySalesRate: number | null;
  snapshotAt: string | null;
  lastCountedQuantity: number | null;
  lastCountedAt: string | null;
  lastCountedBy: string | null;
  pendingCorrection: number | null;
  pendingSince: string | null;
  pendingExpected: number | null;
  pendingDelta: number | null;
}

export type FollowUpTab = "Purchase" | "Warehouse" | "Balance" | "Delete" | "Done";

export interface PurchaseRow {
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  quantity: number | null;
  tasks: number;
  assignedAt: string;
  supplyRequestId: string | null;
  requestNumber: number | null;
  supplierName: string | null;
}

export interface TaskRow {
  taskId: string;
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  taskType: string;
  quantity: number | null;
  note: string | null;
  status: string;
  assignedAt: string;
  assignedByName: string | null;
  approvalStatus: string | null;
  decidedByName: string | null;
  completedAt: string | null;
  completedByName: string | null;
  /** البديل في طلب الحذف. */
  substituteItemNumber?: string | null;
  substituteItemName?: string | null;
}

export interface FollowUpView {
  tab: FollowUpTab;
  from: string;
  to: string;
  counts: { purchase: number; warehouse: number; balance: number; delete: number; done: number };
  rows: (PurchaseRow | TaskRow)[];
}

export interface Supplier {
  supplierId: string;
  name: string;
  phone: string | null;
  notes: string | null;
  isActive: boolean;
}

export interface SupplyRequest {
  requestId: string;
  requestNumber: number;
  status: "Open" | "Purchased" | "Reverted";
  supplierId: string;
  supplierName: string;
  supplierPhone: string | null;
  createdByName: string | null;
  createdAt: string;
  closedByName: string | null;
  closedAt: string | null;
  items: { itemId: string; itemNumber: string; itemName: string | null; quantity: number | null }[];
}

export interface SearchItem {
  itemId: string;
  itemNumber: string;
  itemName: string | null;
}

export interface CustomInventory {
  customInventoryId: string;
  name: string;
  isActive: boolean;
  createdByName: string | null;
  createdAt: string;
  branchIds: string[];
  branchNames: string[];
  /** استُعمل في جلسات جرد: «حذف» يوقفه ويخفيه بدل محوه. */
  used?: boolean;
  items: { itemId: string; itemNumber: string; itemName: string | null }[];
}

export interface AuditRequest {
  requestId: string;
  branchName: string | null;
  recipientUserId: string;
  recipientName: string | null;
  note: string | null;
  status: "Pending" | "Answered" | "Cancelled";
  expired: boolean;
  createdByName: string | null;
  createdAt: string;
  expiresAt: string;
  answeredAt: string | null;
  score: number | null;
  /** تدقيق معالجة النواقص (كما في النظام القديم): الموظف الذي تمت مراجعة معالجته ويوم المعالجة. */
  processedByName?: string | null;
  processedDate?: string | null;
  items: {
    requestItemId: string;
    itemNumber: string;
    itemName: string | null;
    expectedQuantity: number | null;
    receivedQuantity: number | null;
    taskType?: string | null;
    received?: boolean | null;
  }[];
}

export interface AuditCandidate {
  taskId: string;
  itemId: string;
  itemNumber: string;
  itemName: string | null;
  taskType: "Purchase" | "Warehouse" | "Balance";
  quantity: number | null;
}

export interface EfficiencyRow {
  userId: string;
  userName: string | null;
  requests: number;
  answered: number;
  pending: number;
  averageScore: number | null;
}

export interface ProcessingReport {
  from: string;
  to: string;
  overdueDays: number;
  runs: {
    runId: string;
    shortageTypeLabel: string;
    supplyDays: number;
    status: string;
    createdByName: string | null;
    createdAt: string;
    completedAt: string | null;
    itemCount: number;
    processedCount: number;
    tasks: number;
    branches: string | null;
  }[];
  overduePurchases: { itemNumber: string; itemName: string | null; quantity: number | null; assignedAt: string; assignedByName: string | null; supplierName: string | null }[];
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

function range(from: string, to: string): string {
  const params = new URLSearchParams();
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  return params.toString();
}

export function createShortageClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  const json = { ...headers, "Content-Type": "application/json" };
  const get = async <T,>(url: string, fallback: string) => readJson<T>(await fetch(url, { headers }), fallback);
  const send = async <T,>(url: string, body: unknown, fallback: string, method = "POST") =>
    readJson<T>(await fetch(url, { method, headers: json, body: JSON.stringify(body ?? {}) }), fallback);
  return {
    options: () => get<ShortageOptions>("/api/shortages/options", "تعذر تحميل خيارات النواقص."),
    manualCounters: (from: string, to: string, branchIds: string[]) => {
      const params = new URLSearchParams({ from, to, branchIds: branchIds.join(",") });
      return get<{ from: string; to: string; users: (UserOption & { sessions: number })[] }>(`/api/shortages/manual-counters?${params}`, "تعذر تحميل الموظفين.");
    },
    start: (request: StartShortageRequest) =>
      send<{ resumed: boolean; draft: RunHeader | null }>("/api/shortages/runs", request, "تعذر حساب النواقص."),
    run: (runId: string) => get<{ run: RunHeader; items: RunItem[] }>(`/api/shortages/runs/${encodeURIComponent(runId)}`, "تعذر تحميل المعالجة."),
    decide: (runId: string, itemId: string, body: ShortageDecisionBody) =>
      send<{ saved: boolean; substituteItemNumber?: string | null; substituteItemName?: string | null }>(`/api/shortages/runs/${encodeURIComponent(runId)}/items/${encodeURIComponent(itemId)}`, body, "تعذر حفظ قرار الصنف.", "PUT"),
    finalize: (runId: string) =>
      send<{ completed: boolean; tasks: number; items: number }>(`/api/shortages/runs/${encodeURIComponent(runId)}/finalize`, {}, "تعذر التثبيت النهائي."),
    discard: (runId: string) => send<{ discarded: boolean }>(`/api/shortages/runs/${encodeURIComponent(runId)}/discard`, {}, "تعذر إلغاء المعالجة."),
    itemBranches: (itemId: string) =>
      get<{ itemId: string; branches: ItemBranchState[] }>(`/api/shortages/items/${encodeURIComponent(itemId)}/branches`, "تعذر تحميل كميات الفروع."),
    correct: (itemId: string, branchId: string, requestId: string, quantity: number, reason: string, expectedQuantity: number | null = null) =>
      send<{ applied: boolean; pending: boolean }>(`/api/shortages/items/${encodeURIComponent(itemId)}/corrections`, { branchId, requestId, quantity, expectedQuantity, reason }, "تعذر تصحيح الكمية."),
    /** عدد مهام المتابعة المفتوحة لزر «متابعة النواقص (N)»، طلب خلفي بلا «يرجى الانتظار». */
    openTaskCount: async () => {
      const response = await fetch("/api/shortages/tasks/open-count", { headers: { ...headers, [BACKGROUND_HEADER]: "1" } });
      if (!response.ok) return null;
      const open = ((await response.json()) as { open?: unknown }).open;
      return typeof open === "number" ? open : null;
    },
    tasks: (tab: FollowUpTab, from: string, to: string) =>
      get<FollowUpView>(`/api/shortages/tasks?tab=${tab}&${range(from, to)}`, "تعذر تحميل متابعة النواقص."),
    taskAction: (body: { action: string; taskId?: string; itemId?: string; supplyRequestId?: string | null; supplierId?: string; quantity?: number }) =>
      send<{ done: boolean }>("/api/shortages/tasks/actions", body, "تعذر تنفيذ الإجراء."),
    suppliers: () => get<{ suppliers: Supplier[] }>("/api/shortages/suppliers", "تعذر تحميل التجار."),
    saveSupplier: (supplier: Partial<Supplier> & { name: string }) =>
      send<{ suppliers: Supplier[] }>("/api/shortages/suppliers", supplier, "تعذر حفظ التاجر."),
    supplyRequests: (status: string) =>
      get<{ requests: SupplyRequest[] }>(`/api/shortages/supply-requests?status=${encodeURIComponent(status)}`, "تعذر تحميل طلبات التزويد."),
    supplyAction: (requestId: string, action: string, supplierId?: string) =>
      send<{ requests: SupplyRequest[] }>(`/api/shortages/supply-requests/${encodeURIComponent(requestId)}/actions`, { action, supplierId }, "تعذر تنفيذ الإجراء."),
    customInventories: () =>
      get<{ customInventories: CustomInventory[]; branches: { branchId: string; branchName: string; current: boolean }[] }>("/api/shortages/custom-inventories", "تعذر تحميل الجرد المخصص."),
    saveCustomInventory: (body: { customInventoryId: string | null; name: string; isActive: boolean; branchIds: string[]; itemCodes: string[] }) =>
      send<{ customInventories: CustomInventory[]; branches: { branchId: string; branchName: string; current: boolean }[] }>("/api/shortages/custom-inventories", body, "تعذر حفظ الجرد المخصص."),
    searchItems: (query: string) =>
      get<{ items: SearchItem[]; truncated: boolean }>(`/api/shortages/custom-inventories/items?q=${encodeURIComponent(query)}`, "تعذر البحث عن الأصناف."),
    setCustomInventoryActive: (customInventoryId: string, isActive: boolean) =>
      send<{ customInventories: CustomInventory[]; branches: { branchId: string; branchName: string; current: boolean }[] }>(
        `/api/shortages/custom-inventories/${encodeURIComponent(customInventoryId)}/active`, { isActive }, "تعذر تغيير حالة الجرد المخصص."),
    deleteCustomInventory: (customInventoryId: string) =>
      send<{ deleted: boolean; stopped: boolean; view: { customInventories: CustomInventory[]; branches: { branchId: string; branchName: string; current: boolean }[] } }>(
        `/api/shortages/custom-inventories/${encodeURIComponent(customInventoryId)}/delete`, {}, "تعذر حذف الجرد المخصص."),
    auditOptions: (branchId: string) =>
      get<{ branches: { branchId: string; branchName: string; current: boolean }[]; users: UserOption[] }>(
        `/api/shortages/audits/options${branchId ? `?branchId=${encodeURIComponent(branchId)}` : ""}`, "تعذر تحميل خيارات التدقيق."),
    createAudit: (body: { branchId: string; recipientUserId: string; note: string; items: { code: string; expectedQuantity: number }[] }) =>
      send<{ requestId: string }>("/api/shortages/audits", body, "تعذر إنشاء طلب التدقيق."),
    audits: (from: string, to: string) => get<{ requests: AuditRequest[] }>(`/api/shortages/audits?${range(from, to)}`, "تعذر تحميل طلبات التدقيق."),
    cancelAudit: (requestId: string) => send<{ cancelled: boolean }>(`/api/shortages/audits/${encodeURIComponent(requestId)}/cancel`, {}, "تعذر إلغاء الطلب."),
    myAudits: (from: string, to: string) => get<{ requests: AuditRequest[] }>(`/api/shortages/audits/mine?${range(from, to)}`, "تعذر تحميل طلباتك."),
    auditCandidates: (date: string) =>
      get<{ date: string; employees: { name: string; items: AuditCandidate[] }[] }>(
        `/api/shortages/audits/candidates?date=${encodeURIComponent(date)}`, "تعذر تحميل أصناف التدقيق."),
    createTaskAudit: (body: { processedDate: string; processedByName: string; branchId: string; recipientUserId: string; taskIds: string[] }) =>
      send<{ requestId: string }>("/api/shortages/audits", body, "تعذر إنشاء طلب التدقيق."),
    answerAuditYesNo: (requestId: string, items: { requestItemId: string; received: boolean }[]) =>
      send<{ score: number }>(`/api/shortages/audits/${encodeURIComponent(requestId)}/answer`, { items }, "تعذر إرسال الرد."),
    answerAudit: (requestId: string, items: { requestItemId: string; receivedQuantity: number }[]) =>
      send<{ score: number }>(`/api/shortages/audits/${encodeURIComponent(requestId)}/answer`, { items }, "تعذر إرسال الرد."),
    efficiency: (from: string, to: string) =>
      get<{ from: string; to: string; ownOnly: boolean; employees: EfficiencyRow[] }>(`/api/shortages/efficiency?${range(from, to)}`, "تعذر تحميل تقرير الكفاءة."),
    report: (from: string, to: string) => get<ProcessingReport>(`/api/shortages/report?${range(from, to)}`, "تعذر تحميل تقرير معالجة النواقص."),
  };
}

export type ShortageClient = ReturnType<typeof createShortageClient>;

export function isPurchaseRow(row: PurchaseRow | TaskRow): row is PurchaseRow {
  return !("taskId" in row);
}
