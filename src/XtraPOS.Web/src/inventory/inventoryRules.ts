import type { InventoryItem, InventoryType } from "../api/inventory";

export const ADMINISTER = "System.Administer";

export function hasPermission(permissions: readonly string[], code: string): boolean {
  return permissions.includes(ADMINISTER) || permissions.includes(code);
}

export interface InventoryMenuCard {
  id: string;
  label: string;
  /** المرحلة الحالية تفعّل «الجرد» فقط. البقية تظهر «قريباً» لمن يملك صلاحيتها. */
  available: boolean;
}

/** نفس بطاقات XtraPOS Inventory وبنفس الترتيب، كل بطاقة لمن يملك صلاحيتها. */
const MENU: { id: string; label: string; permission: string; available: boolean }[] = [
  { id: "count", label: "الجرد", permission: "Inventory.Count", available: true },
  { id: "define", label: "تحديد جرد", permission: "Inventory.Define", available: false },
  { id: "shortages", label: "معالجة النواقص", permission: "Shortages.Process", available: false },
  { id: "followUp", label: "متابعة النواقص", permission: "Shortages.FollowUp", available: false },
  { id: "items", label: "معالجة الأصناف", permission: "Inventory.Classify", available: false },
  { id: "report", label: "تقرير الجرد", permission: "Inventory.Review", available: false },
  { id: "audit", label: "التدقيق", permission: "Shortages.Audit", available: false },
  { id: "efficiency", label: "تقرير الكفاءة", permission: "Shortages.Efficiency", available: false },
  { id: "shortageReport", label: "تقرير معالجة النواقص", permission: "Shortages.Report", available: false },
];

export function inventoryMenu(permissions: readonly string[]): InventoryMenuCard[] {
  return MENU.filter((card) => hasPermission(permissions, card.permission))
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

/** الصفحات المقترحة لليوم: صفحة الجدول إن وُجدت. الخميس بلا صفحة. */
export function defaultPages(defaultPage: number | null): number[] {
  return defaultPage ? [defaultPage] : [];
}

export function togglePage(pages: readonly number[], page: number): number[] {
  return pages.includes(page) ? pages.filter((value) => value !== page) : [...pages, page].sort((a, b) => a - b);
}
