import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export type StarTrend = "Up" | "Down" | "Steady" | "New" | "Inactive";

export interface StarStanding {
  stars: number;
  windowDays: number;
  windowTotal: number;
  previousTotal: number;
  trend: StarTrend;
  changePercent: number | null;
  nextStarAt: number | null;
  neededForNext: number | null;
  thresholdsAreDefault: boolean;
}

export interface PurchaseLine {
  saleId: string;
  saleNumber: number;
  completedAt: string | null;
  branchName: string | null;
  stationNumber: number | null;
  stationName: string | null;
  cashier: string;
  total: number;
  lineCount: number;
  earn: number | null;
  itemQuantity: number | null;
  itemTotal: number | null;
}

export interface PurchasesPage {
  source: "central" | "local";
  notice: string | null;
  customer: { customerId: string; customerNumber: string | null; name: string; phone: string | null };
  seeTotals: boolean;
  totals: {
    count: number;
    salesTotal: number | null;
    earnTotal: number | null;
    itemNumber: string | null;
    itemName: string | null;
    itemQuantity: number | null;
    itemTotal: number | null;
  };
  stars: StarStanding | null;
  skip: number;
  hasMore: boolean;
  lines: PurchaseLine[];
}

export interface StarThresholds {
  windowDays: number;
  oneStar: number;
  twoStars: number;
  threeStars: number;
  fourStars: number;
  fiveStars: number;
  isDefault: boolean;
  updatedAt: string | null;
  updatedByName: string | null;
}

export interface PurchaseFilters {
  from: string;
  to: string;
  item: string;
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  if (response.status === 403) throw new SetupClientError("لا تملك صلاحية هذه الشاشة.");
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

export function createCustomerPurchasesClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  return {
    async page(customerId: string, filters: PurchaseFilters, skip: number) {
      const params = new URLSearchParams({ skip: String(skip), take: "50" });
      if (filters.from) params.set("from", filters.from);
      if (filters.to) params.set("to", filters.to);
      if (filters.item.trim()) params.set("item", filters.item.trim());
      const response = await fetch(`/api/customers/${encodeURIComponent(customerId)}/purchases?${params}`, { headers });
      return readJson<PurchasesPage>(response, "تعذر تحميل مشتريات الزبون.");
    },
    async thresholds() {
      const response = await fetch("/api/customers/star-thresholds", { headers });
      return readJson<{ thresholds: StarThresholds; canManage: boolean }>(response, "تعذر قراءة حدود النجوم.");
    },
    async saveThresholds(value: Omit<StarThresholds, "isDefault" | "updatedAt" | "updatedByName">) {
      const response = await fetch("/api/customers/star-thresholds", {
        method: "PUT",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify(value),
      });
      return readJson<{ thresholdId: string; relayed: boolean; message: string }>(response, "تعذر حفظ حدود النجوم.");
    },
  };
}

/** النجوم بعدد ثابت 5: الممتلئة ثم الفارغة. */
export function starGlyphs(stars: number) {
  const full = Math.max(0, Math.min(5, Math.round(stars)));
  return "★".repeat(full) + "☆".repeat(5 - full);
}

export function trendText(standing: StarStanding) {
  switch (standing.trend) {
    case "Up": return `في تقدم ${standing.changePercent != null ? `+${standing.changePercent}%` : ""}`.trim();
    case "Down": return `في تأخر ${standing.changePercent != null ? `${standing.changePercent}%` : ""}`.trim();
    case "Steady": return "ثابت";
    case "New": return "زبون جديد في هذه الفترة";
    default: return "لا مشتريات في الفترتين الأخيرتين";
  }
}

/** يدمج صفحة جديدة دون تكرار الفاتورة نفسها. */
export function appendPage(current: PurchaseLine[], next: PurchaseLine[]) {
  const seen = new Set(current.map((line) => line.saleId));
  return [...current, ...next.filter((line) => !seen.has(line.saleId))];
}
