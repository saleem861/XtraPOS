import type { ItemListItem } from "../catalog/types";

export const POS_SEARCH_PAGE_SIZE = 12;

export type SearchSortColumn = "number" | "name" | "price";

export function rankItemMatches(items: ItemListItem[], query: string): ItemListItem[] {
  const text = query.trim();
  const score = (item: ItemListItem) => {
    if (item.itemNumber === text) return 0;
    if (text && (item.itemNumber.startsWith(text) || item.itemName.startsWith(text))) return 1;
    if (text && item.itemName.includes(text)) return 2;
    return 3;
  };

  return [...items].sort((left, right) => score(left) - score(right) || left.itemNumber.localeCompare(right.itemNumber, "ar"));
}

export function sortSearchResults(items: ItemListItem[], column: SearchSortColumn, direction: "asc" | "desc", priceOf: (item: ItemListItem) => number | null): ItemListItem[] {
  const factor = direction === "asc" ? 1 : -1;
  return [...items].sort((left, right) => {
    if (column === "name") return left.itemName.localeCompare(right.itemName, "ar") * factor;
    if (column === "price") {
      const leftPrice = priceOf(left);
      const rightPrice = priceOf(right);
      if (leftPrice === null && rightPrice === null) return 0;
      if (leftPrice === null) return 1;
      if (rightPrice === null) return -1;
      return (leftPrice - rightPrice) * factor;
    }
    return left.itemNumber.localeCompare(right.itemNumber, "ar") * factor;
  });
}
