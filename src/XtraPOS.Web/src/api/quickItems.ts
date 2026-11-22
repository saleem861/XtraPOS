import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export interface QuickItem {
  itemNumber: string;
  itemName: string;
  classification: string | null;
  active?: boolean;
}

export interface QuickItemsManage {
  source: "central" | "local";
  notice: string | null;
  options: { branches: { id: string; name: string }[] };
  branchId: string | null;
  branchName: string | null;
  inherited: boolean;
  set: { setId: string; createdAt: string; createdByName: string | null } | null;
  lines: QuickItem[];
  maxItems: number;
}

export interface QuickItemSuggestion extends QuickItem {
  invoices: number;
  quantity: number;
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  if (response.status === 403) throw new SetupClientError("ضبط الأصناف السريعة للمشرف والمدير.");
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

export function createQuickItemsClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  return {
    async current() {
      const response = await fetch("/api/quick-items/current", { headers });
      return readJson<{ lines: QuickItem[] }>(response, "تعذر قراءة الأصناف السريعة.");
    },
    async manage(branchId: string | null) {
      const query = branchId ? `?branchId=${encodeURIComponent(branchId)}` : "";
      const response = await fetch(`/api/quick-items/manage${query}`, { headers });
      return readJson<QuickItemsManage>(response, "تعذر تحميل الأصناف السريعة.");
    },
    async suggest(branchId: string | null, days = 30, take = 10) {
      const params = new URLSearchParams({ days: String(days), take: String(take) });
      if (branchId) params.set("branchId", branchId);
      const response = await fetch(`/api/quick-items/suggest?${params}`, { headers });
      return readJson<{ items: QuickItemSuggestion[]; days: number }>(response, "تعذر حساب الاقتراح.");
    },
    async save(branchId: string | null, itemNumbers: string[]) {
      const response = await fetch("/api/quick-items/manage", {
        method: "PUT",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ branchId, itemNumbers }),
      });
      return readJson<{ setId: string; relayed: boolean; count: number; message: string }>(response, "تعذر حفظ الأصناف السريعة.");
    },
  };
}

/** رمز تعبيري حسب التصنيف أو الاسم، ولون ثابت لكل تصنيف (بلا صور مخزنة للأصناف). */
const ICONS: { words: string[]; icon: string }[] = [
  { words: ["أرجيل", "شيش", "نرجيل", "hookah", "shisha"], icon: "🪔" },
  { words: ["معسل", "تبغ", "دخان", "تنباك", "tobacco"], icon: "🍂" },
  { words: ["سجائر", "سيجار", "دخ", "cigar"], icon: "🚬" },
  { words: ["فحم", "charcoal"], icon: "🔥" },
  { words: ["ولاع", "قداح", "lighter"], icon: "🔥" },
  { words: ["قهوة", "كوفي", "اسبرسو", "coffee"], icon: "☕" },
  { words: ["شاي", "tea"], icon: "🍵" },
  { words: ["ماء", "مياه", "water"], icon: "💧" },
  { words: ["عصير", "مشروب", "كولا", "صودا", "طاقة", "drink", "juice"], icon: "🥤" },
  { words: ["شوكولا", "حلو", "سكاكر", "بسكويت", "chocolate", "candy"], icon: "🍫" },
  { words: ["شيبس", "مسليات", "chips"], icon: "🍿" },
  { words: ["خبز", "معجنات", "bread"], icon: "🥖" },
  { words: ["حليب", "لبن", "جبن", "milk"], icon: "🥛" },
  { words: ["فواكه", "خضار", "fruit"], icon: "🍎" },
  { words: ["منظف", "صابون", "شامبو", "clean"], icon: "🧴" },
  { words: ["بطاري", "شاحن", "كابل", "battery"], icon: "🔋" },
];

export function quickItemIcon(item: Pick<QuickItem, "classification" | "itemName">) {
  const text = `${item.classification ?? ""} ${item.itemName ?? ""}`.toLowerCase();
  return ICONS.find((entry) => entry.words.some((word) => text.includes(word.toLowerCase())))?.icon ?? "📦";
}

export function quickItemHue(classification: string | null) {
  const text = classification?.trim() || "عام";
  let hash = 0;
  for (const char of text) hash = (hash * 31 + char.charCodeAt(0)) % 360;
  return hash;
}

/** ينقل عنصراً خطوة للأعلى أو للأسفل. */
export function moveItem<T>(list: T[], index: number, step: -1 | 1): T[] {
  const target = index + step;
  if (target < 0 || target >= list.length) return list;
  const next = [...list];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}
