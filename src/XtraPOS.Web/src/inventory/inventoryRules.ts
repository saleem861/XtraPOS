import type { InventoryItem, InventoryType } from "../api/inventory";

export const ADMINISTER = "System.Administer";

export function hasPermission(permissions: readonly string[], code: string): boolean {
  return permissions.includes(ADMINISTER) || permissions.includes(code);
}

export interface InventoryMenuCard {
  id: string;
  label: string;
  /** كل البطاقات مفعّلة. «قريباً» يبقى للبطاقات المستقبلية فقط. */
  available: boolean;
}

/** نفس بطاقات XtraPOS Inventory وبنفس الترتيب، كل بطاقة لمن يملك صلاحيتها. */
const MENU: { id: string; label: string; permission: string; available: boolean }[] = [
  { id: "count", label: "الجرد", permission: "Inventory.Count", available: true },
  { id: "define", label: "تحديد جرد", permission: "Inventory.Define", available: true },
  { id: "shortages", label: "معالجة النواقص", permission: "Shortages.Process", available: true },
  { id: "followUp", label: "متابعة النواقص", permission: "Shortages.FollowUp", available: true },
  { id: "items", label: "معالجة الأصناف", permission: "Inventory.Classify", available: true },
  { id: "report", label: "تقرير الجرد", permission: "Inventory.Review", available: true },
  { id: "audit", label: "التدقيق", permission: "Shortages.Audit", available: true },
  { id: "efficiency", label: "تقرير الكفاءة", permission: "Shortages.Efficiency", available: true },
  { id: "shortageReport", label: "تقرير معالجة النواقص", permission: "Shortages.Report", available: true },
];

export function inventoryMenu(permissions: readonly string[]): InventoryMenuCard[] {
  return MENU.filter((card) => hasPermission(permissions, card.permission)
    // «كمية صنف» داخل «معالجة الأصناف»، فمن يملك التصحيح وحده يرى البطاقة أيضاً.
    || (card.id === "items" && hasPermission(permissions, "Inventory.CorrectQuantity")))
    .map(({ id, label, available }) => ({ id, label, available }));
}

export function canOpenInventory(permissions: readonly string[]): boolean {
  return hasPermission(permissions, "Inventory.Count");
}

export const INVENTORY_TYPES: { value: InventoryType; label: string }[] = [
  { value: "Important", label: "جرد الأصناف المهمة" },
  { value: "Periodic", label: "الجرد الدوري" },
  { value: "Manual", label: "جرد يدوي" },
];

export function inventoryTypeLabel(type: InventoryType): string {
  if (type === "Custom") return "جرد مخصص";
  return INVENTORY_TYPES.find((option) => option.value === type)?.label ?? type;
}

const ARABIC_DIGITS = "٠١٢٣٤٥٦٧٨٩";
const PERSIAN_DIGITS = "۰۱۲۳۴۵۶۷۸۹";

export function normalizeDigits(text: string): string {
  return [...text].map((char) => {
    const arabic = ARABIC_DIGITS.indexOf(char);
    if (arabic >= 0) return String(arabic);
    const persian = PERSIAN_DIGITS.indexOf(char);
    if (persian >= 0) return String(persian);
    return char;
  }).join("");
}

/** الصفر مسموح، السالب والنص غير الرقمي مرفوضان. يقبل الأرقام العربية والفاصلة. */
export function parseCountInput(text: string): number | null {
  const normalized = normalizeDigits(text.trim()).replace("٫", ".").replace(",", ".");
  if (!/^\d+(\.\d{1,3})?$/.test(normalized)) return null;
  const value = Number(normalized);
  return Number.isFinite(value) && value >= 0 ? value : null;
}

export function stepQuantity(current: string, step: 1 | -1): string {
  const value = parseCountInput(current) ?? 0;
  return String(Math.max(0, Math.round((value + step) * 1000) / 1000));
}

export type CountTab = "current" | "counted";

export function filterItems(items: readonly InventoryItem[], tab: CountTab, query: string): InventoryItem[] {
  const text = normalizeDigits(query.trim()).toLowerCase();
  return items.filter((item) => {
    if (tab === "current" ? item.isCounted : !item.isCounted) return false;
    if (!text) return true;
    return item.itemNumber.toLowerCase().includes(text) || (item.itemName ?? "").toLowerCase().includes(text);
  });
}

export function formatQuantity(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return Number.isInteger(value) ? String(value) : value.toFixed(3).replace(/\.?0+$/, "");
}

export function formatDifference(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  if (value === 0) return "0";
  return value > 0 ? `+${formatQuantity(value)}` : `−${formatQuantity(Math.abs(value))}`;
}

/**
 * تصحيح كمية فرع آخر (قرار المالك): الفارق = المعدودة − المفترضة، والفرع يضيفه إلى رصيده الحالي.
 * بلا رصيد معروف للفرع لا يوجد فارق، فيكتب الفرع المعدودة كما هي.
 */
export function correctionDifference(expected: number | null | undefined, counted: number | null | undefined): number | null {
  if (expected === null || expected === undefined || counted === null || counted === undefined) return null;
  return Math.round((counted - expected) * 10000) / 10000;
}

/** الصفحات المقترحة لليوم: صفحة الجدول إن وُجدت. الخميس بلا صفحة. */
export function defaultPages(defaultPage: number | null): number[] {
  return defaultPage ? [defaultPage] : [];
}

export function togglePage(pages: readonly number[], page: number): number[] {
  return pages.includes(page) ? pages.filter((value) => value !== page) : [...pages, page].sort((a, b) => a - b);
}

/** تاريخ محلي بصيغة YYYY-MM-DD لحقول التاريخ. */
export function isoDate(value: Date): string {
  const month = String(value.getMonth() + 1).padStart(2, "0");
  const day = String(value.getDate()).padStart(2, "0");
  return `${value.getFullYear()}-${month}-${day}`;
}

/** الافتراضي في «تقرير الجرد»: آخر 7 أيام حتى اليوم. */
export function defaultReportRange(today: Date): { from: string; to: string } {
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 6);
  return { from: isoDate(start), to: isoDate(today) };
}

/** عدد صحيح اختياري: الفارغ = null، وغير الصالح = undefined. */
export function parseOptionalInteger(text: string, min: number, max: number): number | null | undefined {
  const normalized = normalizeDigits(text.trim());
  if (!normalized) return null;
  if (!/^\d+$/.test(normalized)) return undefined;
  const value = Number(normalized);
  return value >= min && value <= max ? value : undefined;
}

export function sessionStatusLabel(status: string): string {
  if (status === "Completed") return "مثبّت";
  if (status === "Open") return "غير مثبّت";
  if (status === "Discarded") return "ملغى";
  return status;
}

export type ReportLineTab = "differences" | "counted" | "uncounted";

export function filterReportLines<T extends { isCounted: boolean; difference: number | null; expectedQuantity: number | null }>(
  lines: readonly T[],
  tab: ReportLineTab,
): T[] {
  if (tab === "uncounted") return lines.filter((line) => !line.isCounted);
  if (tab === "counted") return lines.filter((line) => line.isCounted);
  return lines.filter((line) => line.isCounted && (line.expectedQuantity === null || (line.difference ?? 0) !== 0));
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return `${isoDate(date)} ${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

/** أرقام أصناف من نص ملصق: سطر أو فاصلة أو مسافة لكل رقم، بلا تكرار. */
export function parseItemCodes(text: string): string[] {
  const codes = normalizeDigits(text).split(/[\s,،;]+/).map((code) => code.trim()).filter(Boolean);
  return [...new Set(codes)];
}

/** كمية اختيارية في قرار النواقص: الفارغ = لا شيء، وغير الصالح = undefined. */
export function parseOptionalQuantity(text: string): number | null | undefined {
  if (!text.trim()) return null;
  const value = parseCountInput(text);
  return value === null ? undefined : value;
}

export const SHORTAGE_TYPES: { value: InventoryType; label: string }[] = [
  { value: "Important", label: "الأصناف المهمة" },
  { value: "Periodic", label: "الجرد الدوري" },
  { value: "Manual", label: "الجرد اليدوي" },
  { value: "Custom", label: "الجرد المخصص" },
];
