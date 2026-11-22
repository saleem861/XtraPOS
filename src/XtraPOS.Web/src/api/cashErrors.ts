import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export type CashErrorStatus = "Open" | "Deferred" | "Settled" | "Offset" | "Waived";
export type CashErrorActionType = "Offset" | "Settle" | "Defer" | "Note";
export type SettleMethod = "Cash" | "Payroll" | "Other";

/** خطأ جرار (أو رصيد متبقٍ من مقاصة). المبلغ بإشارة: النقص سالب والزيادة موجبة. */
export interface CashErrorItem {
  itemId: string;
  kind: "Error" | "Residual";
  employeeId: string;
  employeeName: string;
  branchId: string;
  branchName: string | null;
  stationNumber: number | null;
  stationName: string | null;
  amount: number;
  occurredAt: string;
  effectiveAt: string;
  justification: string | null;
  decisionCode: string | null;
  status: CashErrorStatus;
  closedBy: string | null;
  closedType: string | null;
  closedMethod: string | null;
  closedAt: string | null;
  closedByName: string | null;
  deferredTo: string | null;
  noteCount: number;
  employeeNumber?: string | null;
}

export interface CashErrorSummary {
  count: number;
  openCount: number;
  shortage: number;
  surplus: number;
  waived: number;
  settled: number;
  openNet: number;
}

export interface CashErrorBalance {
  employeeId: string;
  employeeName: string;
  summary: CashErrorSummary;
  employeeNumber?: string | null;
}

export interface CashErrorOptions {
  branches: { id: string; name: string }[];
  stations: { id: string; branchId: string; stationNumber: number; name: string }[];
  users: { id: string; name: string }[];
}

export interface CashErrorList {
  source: "central" | "local";
  notice: string | null;
  canManageAll: boolean;
  scopedBranchId: string | null;
  scopedBranchName: string | null;
  summary: CashErrorSummary;
  employees: CashErrorBalance[];
  items: CashErrorItem[];
  options: CashErrorOptions;
  employeeNumbers?: Record<string, string>;
}

export interface CashErrorAction {
  actionId: string;
  type: CashErrorActionType;
  employeeId: string;
  amount: number;
  method: SettleMethod | null;
  deferTo: string | null;
  note: string | null;
  actorName: string | null;
  createdAt: string;
  items: string[];
}

export interface CashErrorStatement {
  source: "central" | "local";
  notice: string | null;
  employee: { employeeId: string; name: string; number?: string | null };
  from: string;
  to: string;
  summary: CashErrorSummary;
  items: CashErrorItem[];
  actions: CashErrorAction[];
}

export interface CashErrorFilters {
  from: string;
  to: string;
  employeeId: string;
  branchId: string;
  stationId: string;
  type: "" | "Shortage" | "Surplus";
  status: "" | "Unsettled" | CashErrorStatus;
}

export interface CashErrorActionInput {
  type: CashErrorActionType;
  employeeId: string;
  itemIds: string[];
  method?: SettleMethod;
  deferTo?: string;
  note?: string;
}

export interface CashErrorActionResult {
  actionId: string;
  type: CashErrorActionType;
  amount: number;
  relayed: boolean;
  message: string;
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  if (response.status === 403) throw new SetupClientError("هذه الشاشة للمشرف والمدير فقط.");
  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { message?: string };
      if (body.message?.trim()) message = body.message;
    } catch {
      message = fallback;
    }
    throw new SetupClientError(message);
  }
  return (await response.json()) as T;
}

export function cashErrorQuery(filters: CashErrorFilters) {
  const params = new URLSearchParams();
  if (filters.from) params.set("from", filters.from);
  if (filters.to) params.set("to", filters.to);
  if (filters.employeeId) params.set("employeeId", filters.employeeId);
  if (filters.branchId) params.set("branchId", filters.branchId);
  if (filters.stationId) params.set("stationId", filters.stationId);
  if (filters.type) params.set("type", filters.type);
  if (filters.status) params.set("status", filters.status);
  return params;
}

export function createCashErrorClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  return {
    async list(filters: CashErrorFilters) {
      const response = await fetch(`/api/cash-errors?${cashErrorQuery(filters)}`, { headers });
      return readJson<CashErrorList>(response, "تعذر تحميل أخطاء الموظفين.");
    },
    async statement(employeeId: string, from: string, to: string) {
      const params = new URLSearchParams({ employeeId, from, to });
      const response = await fetch(`/api/cash-errors/statement?${params}`, { headers });
      return readJson<CashErrorStatement>(response, "تعذر تحميل كشف الموظف.");
    },
    async act(input: CashErrorActionInput) {
      const response = await fetch("/api/cash-errors/actions", {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify(input),
      });
      return readJson<CashErrorActionResult>(response, "تعذر حفظ الإجراء.");
    },
    async exportExcel(filters: CashErrorFilters) {
      const response = await fetch(`/api/cash-errors/export?${cashErrorQuery(filters)}`, { headers });
      if (!response.ok) await readJson<never>(response, "تعذر تصدير أخطاء الموظفين.");
      const name = response.headers.get("X-Export-File-Name");
      return { blob: await response.blob(), fileName: name ? decodeURIComponent(name) : "أخطاء-الموظفين.xlsx" };
    },
  };
}

export type CashErrorClient = ReturnType<typeof createCashErrorClient>;

// ---------------------------------------------------------------------------
// منطق العرض (يُختبر دون خادم)
// ---------------------------------------------------------------------------

export const STATUS_TEXT: Record<CashErrorStatus, string> = {
  Open: "غير مسوّى",
  Deferred: "مؤجل",
  Settled: "مسوّى",
  Offset: "مُقاص",
  Waived: "معفى",
};

export const METHOD_TEXT: Record<SettleMethod, string> = {
  Cash: "نقداً من الموظف",
  Payroll: "خصم من الراتب",
  Other: "أخرى",
};

export const ACTION_TEXT: Record<CashErrorActionType, string> = {
  Offset: "مقاصة",
  Settle: "تسوية",
  Defer: "تأجيل",
  Note: "ملاحظة",
};

export function isOpen(item: CashErrorItem) {
  return item.status === "Open" || item.status === "Deferred";
}

export function signedMoney(value: number) {
  const fixed = Math.abs(value).toFixed(2);
  return value < 0 ? `-${fixed}` : value > 0 ? `+${fixed}` : fixed;
}

export function kindText(item: CashErrorItem) {
  if (item.kind === "Residual") return "متبقٍ من مقاصة";
  return item.amount < 0 ? "نقص" : "زيادة";
}

export function decisionText(code: string | null) {
  if (code === "KeepShortage") return "إبقاء النقص";
  if (code === "KeepSurplus") return "إبقاء الزيادة";
  if (code === "Waive") return "إعفاء الموظف";
  return "—";
}

/** يضيف بنداً إلى الاختيار أو يزيله. الاختيار لموظف واحد ولبنود غير مسوّاة فقط. */
export function toggleSelection(selected: string[], item: CashErrorItem, all: CashErrorItem[]): { selected: string[]; error: string } {
  if (selected.includes(item.itemId)) return { selected: selected.filter((id) => id !== item.itemId), error: "" };
  if (!isOpen(item)) return { selected, error: "هذا الخطأ مسوّى أو معفى ولا يُختار." };
  const first = all.find((row) => row.itemId === selected[0]);
  if (first && first.employeeId !== item.employeeId) return { selected, error: "اختر أخطاء موظف واحد في كل مرة." };
  return { selected: [...selected, item.itemId], error: "" };
}

export function selectionNet(selected: string[], all: CashErrorItem[]) {
  return all.filter((row) => selected.includes(row.itemId)).reduce((sum, row) => sum + row.amount, 0);
}

export function monthStart(date = new Date()) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-01`;
}

export function monthEnd(date = new Date()) {
  const last = new Date(date.getFullYear(), date.getMonth() + 1, 0);
  return `${last.getFullYear()}-${String(last.getMonth() + 1).padStart(2, "0")}-${String(last.getDate()).padStart(2, "0")}`;
}

export function nextMonth(date = new Date()) {
  const next = new Date(date.getFullYear(), date.getMonth() + 1, 1);
  return `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, "0")}`;
}
