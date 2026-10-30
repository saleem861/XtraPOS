import type { ItemInventory } from "./types";

export function formatPrice(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return "—";
  }

  return value.toFixed(2);
}

export function formatQuantity(value: number): string {
  const text = value.toFixed(4).replace(/\.?0+$/, "");
  return text === "" || text === "-" ? "0" : text;
}

export function formatFactor(value: number): string {
  return formatQuantity(value);
}

export function inventoryStatus(inventory: ItemInventory): string | null {
  if (!inventory.branchReady || inventory.quantityState === "BranchPending") {
    return "لم يتم تهيئة مخزون هذا الفرع بعد";
  }

  if (inventory.quantityState !== "Known" || inventory.onHandQuantity === null || inventory.onHandQuantity === undefined) {
    return "غير مجرود";
  }

  return null;
}

export function blank(value: string | null | undefined): string {
  const text = value?.trim() ?? "";
  return text.length === 0 ? "—" : text;
}
