import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ItemCatalogClient, ItemListItem, ItemQuery } from "../catalog/types";
import { formatPrice } from "../catalog/format";
import { ItemCatalogScreen } from "./ItemCatalogScreen";

const activeItem: ItemListItem = {
  itemId: "item-1",
  itemNumber: "1360",
  itemName: "مولاس",
  itemType: "أساس",
  manufacturer: "الشركة",
  classification: "مواد",
  isActive: true,
  isProhibited: true,
};

const inactiveItem: ItemListItem = {
  itemId: "item-2",
  itemNumber: "50",
  itemName: "صنف قديم",
  itemType: "تاريخي",
  isActive: false,
  isProhibited: false,
};

function client(items: ItemListItem[]): ItemCatalogClient {
  return {
    loadItems: vi.fn(async (query: ItemQuery) => ({
      items: items.filter((item) => {
        const matchesActivity = query.activity === "all"
          || (query.activity === "active" ? item.isActive : !item.isActive);
        const text = query.search;
        const matchesSearch = text.length === 0 || item.itemNumber.includes(text) || item.itemName.includes(text);
        return matchesActivity && matchesSearch;
      }),
      totalCount: 60,
      page: query.page,
      pageSize: query.pageSize,
    })),
    loadDetail: vi.fn(async () => ({
      itemId: activeItem.itemId,
      itemNumber: activeItem.itemNumber,
      itemName: activeItem.itemName,
      itemType: activeItem.itemType,
      manufacturer: activeItem.manufacturer,
      color: null,
      taste: null,
      size: null,
      packing: null,
      classification: activeItem.classification,
      countryOfOrigin: null,
      productType: null,
      isActive: true,
      isProhibited: true,
      isPublishedOnline: false,
      parentItemNumber: null,
      parentItemName: null,
    })),
    loadPrices: vi.fn(async () => [
      { priceCode: "Retail", priceName: "السعر العادي", price: null },
      { priceCode: "Dealer", priceName: "سعر الجملة", price: 0 },
    ]),
    loadUnits: vi.fn(async () => ({
      units: [],
      ownsPhysicalStock: true,
      stockOwnerItemNumber: "1360",
      stockOwnerItemName: "مولاس",
      baseQuantityFactor: 1,
    })),
    loadInventory: vi.fn(async () => ({
      branchReady: false,
      isDerived: true,
      stockOwnerItemNumber: "1360",
      stockOwnerItemName: "مولاس",
      baseQuantityFactor: 4,
      quantityState: "BranchPending" as const,
      onHandQuantity: null,
    })),
    loadMovements: vi.fn(async () => []),
    loadEditForm: vi.fn(async () => ({
      topology: "Standalone" as const,
      centralReachable: false,
      masterReadOnly: false,
      masterReadOnlyMessage: null,
      canEditMaster: false,
      canEditBranchState: false,
      canEditBranchPrice: false,
      itemName: activeItem.itemName,
      itemType: activeItem.itemType,
      manufacturer: activeItem.manufacturer,
      color: null,
      taste: null,
      size: null,
      packing: null,
      classification: activeItem.classification,
      countryOfOrigin: null,
      productType: null,
      parentItemId: null,
      isActive: true,
      isProhibited: true,
      isPublishedOnline: false,
      rowVersion: null,
      unitCost: null,
      isAvailable: true,
      branchActive: true,
      prices: [],
    })),
    saveMaster: vi.fn(async () => ({ rowVersion: "v2", changeVersion: null })),
    saveUnits: vi.fn(async () => ({ rowVersion: "v2", changeVersion: null })),
    saveDefaultPrice: vi.fn(async () => ({ rowVersion: "v2", changeVersion: null })),
    saveBranchPrice: vi.fn(async () => undefined),
    saveBranchState: vi.fn(async () => undefined),
  };
}

function CatalogHarness({ api }: { api: ItemCatalogClient }) {
  const [stable] = useState(api);
  return <ItemCatalogScreen client={stable} onHome={() => undefined} debounceMs={0} />;
}

afterEach(() => cleanup());

describe("item catalog", () => {
  it("starts on active items and searches without loading every row", async () => {
    const user = userEvent.setup();
    const api = client([activeItem, inactiveItem]);
    render(<CatalogHarness api={api} />);

    expect((await screen.findAllByText("1360")).length).toBeGreaterThan(0);
    expect(screen.queryByText("صنف قديم")).toBeNull();
    expect(screen.getAllByText("ممنوع البيع").length).toBeGreaterThan(0);
    const firstQuery = vi.mocked(api.loadItems).mock.calls[0][0];
    expect(firstQuery.activity).toBe("active");
    expect(firstQuery.page).toBe(1);
    expect(firstQuery.pageSize).toBe(50);

    await user.type(screen.getByPlaceholderText("ابحث برقم الصنف أو الاسم"), "مولاس");
    await waitFor(() => {
      const last = vi.mocked(api.loadItems).mock.calls.at(-1)?.[0];
      expect(last?.search).toBe("مولاس");
    });
  });

  it("pages results and opens tabs for an inactive-capable list", async () => {
    const user = userEvent.setup();
    const api = client([activeItem, inactiveItem]);
    render(<CatalogHarness api={api} />);
    await screen.findAllByText("1360");

    await user.selectOptions(screen.getByLabelText("تصفية الحالة"), "inactive");
    await waitFor(() => expect(screen.getAllByText("غير نشط").length).toBeGreaterThan(0));
    expect(screen.queryByText("1360")).toBeNull();

    await user.selectOptions(screen.getByLabelText("تصفية الحالة"), "all");
    await screen.findAllByText("1360");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await waitFor(() => {
      const last = vi.mocked(api.loadItems).mock.calls.at(-1)?.[0];
      expect(last?.page).toBe(2);
    });

    await user.click(screen.getAllByText("1360")[0]);
    expect(await screen.findByRole("heading", { name: "تفاصيل الصنف" })).toBeTruthy();
    await user.click(screen.getByRole("tab", { name: "الأسعار" }));
    expect(screen.getByText(formatPrice(0))).toBeTruthy();
    expect(screen.getAllByText("—").length).toBeGreaterThan(0);
    await user.click(screen.getByRole("tab", { name: "المخزون" }));
    expect(screen.getByText("لم يتم تهيئة مخزون هذا الفرع بعد")).toBeTruthy();
    expect(screen.getByText("الكمية تُحتسب من مخزون الوحدة الأساسية")).toBeTruthy();
    expect(screen.queryByText("0.00")).toBeNull();
    await user.click(screen.getByRole("tab", { name: "الحركة" }));
    expect(screen.getByText("لا توجد حركات مخزون")).toBeTruthy();
    await user.click(screen.getByRole("tab", { name: "الوحدات" }));
    expect(screen.getByText("لا توجد وحدة داخلية مرتبطة بهذا الصنف")).toBeTruthy();
  });

  it("resets to the first page when search or status changes", async () => {
    const user = userEvent.setup();
    const api = client([activeItem, inactiveItem]);
    render(<CatalogHarness api={api} />);
    await screen.findAllByText("1360");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await waitFor(() => {
      expect(vi.mocked(api.loadItems).mock.calls.at(-1)?.[0].page).toBe(2);
    });

    await user.type(screen.getByPlaceholderText("ابحث برقم الصنف أو الاسم"), "1");
    await waitFor(() => {
      const last = vi.mocked(api.loadItems).mock.calls.at(-1)?.[0];
      expect(last?.search).toBe("1");
      expect(last?.page).toBe(1);
    });

    await user.click(screen.getByRole("button", { name: "التالي" }));
    await waitFor(() => {
      expect(vi.mocked(api.loadItems).mock.calls.at(-1)?.[0].page).toBe(2);
    });
    await user.selectOptions(screen.getByLabelText("تصفية الحالة"), "all");
    await waitFor(() => {
      const last = vi.mocked(api.loadItems).mock.calls.at(-1)?.[0];
      expect(last?.activity).toBe("all");
      expect(last?.page).toBe(1);
    });

    await user.click(screen.getByRole("button", { name: "التالي" }));
    await waitFor(() => {
      expect(vi.mocked(api.loadItems).mock.calls.at(-1)?.[0].page).toBe(2);
    });
    await user.selectOptions(screen.getByLabelText("حجم الصفحة"), "25");
    await waitFor(() => {
      const last = vi.mocked(api.loadItems).mock.calls.at(-1)?.[0];
      expect(last?.pageSize).toBe(25);
      expect(last?.page).toBe(1);
    });
  });

  it("shows a load failure instead of an empty search result", async () => {
    const api = client([]);
    vi.mocked(api.loadItems).mockRejectedValue(new Error("503"));
    render(<CatalogHarness api={api} />);

    expect(await screen.findByText("تعذر تحميل الأصناف")).toBeTruthy();
    expect(screen.queryByText("لا توجد أصناف مطابقة للبحث")).toBeNull();
  });
});
