import { deviceHeaders } from "./client";
import { parseDecimal } from "../cart/decimal";
import type { DeviceSession } from "../types/setup";

/**
 * Cash sessions are authoritative in the branch database.
 * This client only reads and posts them. It does not store a session in IndexedDB.
 */
export class CashSessionClientError extends Error {
  readonly unavailable: boolean;
  readonly session: CashSessionState | null;

  constructor(message: string, unavailable: boolean, session: CashSessionState | null = null) {
    super(message);
    this.name = "CashSessionClientError";
    this.unavailable = unavailable;
    this.session = session;
  }
}

export interface CashSessionState {
  cashSessionId: string;
  status: string;
  openedByDisplayName: string;
  openedAt: string;
  openingCashAmount: string;
  expectedCashAmount: string | null;
  countedCashAmount: string | null;
  cashDifference: string | null;
  closedAt: string | null;
}

export function isNonNegativeMoney(value: string): boolean {
  const text = value.trim();
  if (!/^\d+(\.\d{1,4})?$/.test(text)) return false;
  try {
    return parseDecimal(text) >= 0n;
  } catch {
    return false;
  }
}

export function createCashSessionClient(session: DeviceSession) {
  const headers = { "Content-Type": "application/json", ...deviceHeaders(session) };
  return {
    current: () => loadCurrent(session),
    open: (openingCashAmount: string) =>
      send("/api/pos/cash-session/open", `{"openingCashAmount":${openingCashAmount.trim()}}`, headers),
    close: (countedCashAmount: string) =>
      send("/api/pos/cash-session/close", `{"countedCashAmount":${countedCashAmount.trim()}}`, headers),
  };
}

export interface CurrentCashSession {
  session: CashSessionState | null;
  expectedCashPreview: string | null;
}

async function loadCurrent(session: DeviceSession): Promise<CurrentCashSession> {
  let response: Response;
  try {
    response = await fetch("/api/pos/cash-session/current", { credentials: "include", headers: deviceHeaders(session) });
  } catch {
    throw new CashSessionClientError("حالة الوردية غير متاحة. السلة محفوظة على هذا الجهاز.", true);
  }
  if (!response.ok) {
    throw new CashSessionClientError("حالة الوردية غير متاحة. السلة محفوظة على هذا الجهاز.", true);
  }
  const body = await response.json() as { session?: unknown; expectedCashPreview?: unknown };
  return {
    session: mapSession(body.session),
    expectedCashPreview: body.expectedCashPreview == null ? null : money(body.expectedCashPreview),
  };
}

async function send(path: string, body: string, headers: HeadersInit): Promise<CashSessionState> {
  let response: Response;
  try {
    response = await fetch(path, { method: "POST", credentials: "include", headers, body });
  } catch {
    throw new CashSessionClientError("خادم الفرع غير متاح. السلة محفوظة على هذا الجهاز.", true);
  }
  const payload = await readBody(response);
  const mapped = mapSession(payload.session);
  if (response.status === 409 && mapped) {
    throw new CashSessionClientError(payload.message || "يوجد صندوق مفتوح لهذا الجهاز.", false, mapped);
  }
  if (!response.ok || !mapped) {
    const unavailable = response.status >= 500 || response.status === 0;
    throw new CashSessionClientError(payload.message || "تعذر تحديث الصندوق.", unavailable);
  }
  return mapped;
}

async function readBody(response: Response): Promise<{ message?: string; session?: unknown }> {
  try {
    return await response.json() as { message?: string; session?: unknown };
  } catch {
    return {};
  }
}

function mapSession(value: unknown): CashSessionState | null {
  if (!value || typeof value !== "object") return null;
  const row = value as Record<string, unknown>;
  if (typeof row.cashSessionId !== "string" || typeof row.openedAt !== "string") return null;
  return {
    cashSessionId: row.cashSessionId,
    status: String(row.status ?? ""),
    openedByDisplayName: String(row.openedByDisplayName ?? ""),
    openedAt: row.openedAt,
    openingCashAmount: money(row.openingCashAmount),
    expectedCashAmount: row.expectedCashAmount == null ? null : money(row.expectedCashAmount),
    countedCashAmount: row.countedCashAmount == null ? null : money(row.countedCashAmount),
    cashDifference: row.cashDifference == null ? null : money(row.cashDifference),
    closedAt: typeof row.closedAt === "string" ? row.closedAt : null,
  };
}

function money(value: unknown): string {
  return typeof value === "number" || typeof value === "string" ? String(value) : "";
}
