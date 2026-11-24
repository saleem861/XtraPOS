import { describe, expect, it } from "vitest";
import { PAGE_SIZES, pageCount, pageSlice } from "./Pager";

describe("inventory paging", () => {
  it("offers 10, 25 and 50 per page", () => {
    expect([...PAGE_SIZES]).toEqual([10, 25, 50]);
  });

  it("slices pages and keeps the page in range", () => {
    const items = Array.from({ length: 23 }, (_, index) => index + 1);
    expect(pageCount(items.length, 10)).toBe(3);
    expect(pageSlice(items, 1, 10)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    expect(pageSlice(items, 3, 10)).toEqual([21, 22, 23]);
    expect(pageSlice(items, 9, 10)).toEqual([21, 22, 23]);
    expect(pageCount(0, 25)).toBe(1);
    expect(pageSlice([], 1, 25)).toEqual([]);
  });
});
