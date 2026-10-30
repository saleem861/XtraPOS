import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createItemClient } from "../api/items";
import type { ItemCatalogClient, ItemDetail, ItemEditForm, ItemPrice } from "../catalog/types";
import { ItemDetailScreen } from "./ItemDetailScreen";
import type { DeviceSession } from "../types/setup";

const detail: ItemDetail = {
  itemId: "item-1",
  itemNumber: "1087",
  itemName: "مولاس",
  itemType: "أساس",
  manufacturer: "الشركة",
  color: null,
  taste: null,
  size: null,
  packing: null,
  classification: "مواد",
  countryOfOrigin: null,
  productType: null,
  isActive: true,
  isProhibited: false,
  isPublishedOnline: false,
  parentItemNumber: null,
  parentItemName: null,
};

const prices: ItemPrice[] = [
  { priceCode: "Price0", priceName: "Price0", price: 25, defaultPrice: 25, branchPrice: 23 },
];

function form(overrides: Partial<ItemEditForm>): ItemEditForm {
  return {
    topology: "Standalone",
    centralReachable: false,
    masterReadOnly: false,
    masterReadOnlyMessage: null,
    canEditMaster: true,
    canEditBranchState: true,
    canEditBranchPrice: true,
    itemName: detail.itemName,
    itemType: detail.itemType,
    manufacturer: detail.manufacturer,
    isActive: true,
    isProhibited: false,
    isPublishedOnline: false,
    rowVersion: "version-1",
    unitCost: 4,
    isAvailable: true,
    branchActive: true,
    prices: [{ priceCode: "Price0", priceName: "Price0", defaultPrice: 25, branchPrice: 23, defaultRowVersion: "price-1" }],
    ...overrides,
  };
}

function client(editForm: ItemEditForm): ItemCatalogClient {
  return {
    loadItems: vi.fn(),
    loadDetail: vi.fn(async () => detail),
    loadPrices: vi.fn(async () => prices),
    loadUnits: vi.fn(async () => ({
      units: [],
      ownsPhysicalStock: true,
      stockOwnerItemNumber: "1087",
      stockOwnerItemName: "مولاس",
      baseQuantityFactor: 1,
    })),
    loadInventory: vi.fn(async () => ({
      branchReady: false,
      isDerived: false,
      baseQuantityFactor: 1,
      quantityState: "BranchPending" as const,
      onHandQuantity: null,
    })),
    loadMovements: vi.fn(async () => []),
    loadEditForm: vi.fn(async () => editForm),
    saveMaster: vi.fn(async () => ({ rowVersion: "version-2", changeVersion: 12 })),
    saveUnits: vi.fn(async () => ({ rowVersion: "version-2", changeVersion: 13 })),
    saveDefaultPrice: vi.fn(async () => ({ rowVersion: "price-2", changeVersion: 14 })),
    saveBranchPrice: vi.fn(async () => undefined),
    saveBranchState: vi.fn(async () => undefined),
  };
}

function Harness({ api }: { api: ItemCatalogClient }) {
  const [stable] = useState(api);
  return <ItemDetailScreen client={stable} itemId="item-1" onHome={() => undefined} onBack={() => undefined} />;
}

afterEach(() => cleanup());

describe("item ownership screen", () => {
  it("shows central and branch ownership on a linked branch", async () => {
    const user = userEvent.setup();
    const api = client(form({ topology: "CentralLinked", centralReachable: true }));
    render(<Harness api={api} />);

    expect(await screen.findByRole("heading", { name: /البيانات الأساسية للصنف/ })).toBeTruthy();
    expect(screen.getByText("بيانات مركزية")).toBeTruthy();
    expect(screen.getByText("خاص بهذا الفرع")).toBeTruthy();
    await user.click(screen.getByRole("tab", { name: "الأسعار" }));
    expect(screen.getByText("السعر الأساسي")).toBeTruthy();
    expect(screen.getAllByText("سعر هذا الفرع").length).toBeGreaterThan(0);
    expect(screen.getByText("25.00")).toBeTruthy();
    expect(screen.getByText("23.00")).toBeTruthy();
    await user.click(screen.getByRole("tab", { name: "الوحدات" }));
    expect(screen.getAllByText("بيانات مركزية").length).toBeGreaterThan(0);
  });

  it("keeps master fields read-only when central is offline", async () => {
    const api = client(form({
      topology: "CentralLinked",
      centralReachable: false,
      masterReadOnly: true,
      canEditMaster: false,
      masterReadOnlyMessage: "تعذر الاتصال بالإدارة المركزية.\nيمكن عرض بيانات الصنف، لكن تعديل البيانات الأساسية يحتاج اتصالًا بالمركز.",
    }));
    render(<Harness api={api} />);

    expect(await screen.findByText(/تعذر الاتصال بالإدارة المركزية/)).toBeTruthy();
    expect(screen.getByText(/تعديل البيانات الأساسية يحتاج اتصالًا بالمركز/)).toBeTruthy();
    expect(screen.getByLabelText("اسم الصنف")).toHaveProperty("disabled", true);
    expect(screen.queryByRole("button", { name: "حفظ البيانات الأساسية" })).toBeNull();
    expect(screen.getByLabelText("تكلفة الوحدة")).toHaveProperty("disabled", false);
  });

  it("lets a standalone branch edit its own master fields through the branch API", async () => {
    const user = userEvent.setup();
    const api = client(form({ topology: "Standalone", canEditMaster: true }));
    render(<Harness api={api} />);

    const name = await screen.findByLabelText("اسم الصنف");
    expect(name).toHaveProperty("disabled", false);
    expect(screen.queryByText("بيانات مركزية")).toBeNull();
    await user.clear(name);
    await user.type(name, "اسم محلي");
    await user.click(screen.getByRole("button", { name: "حفظ البيانات الأساسية" }));
    expect(api.saveMaster).toHaveBeenCalledWith("item-1", expect.objectContaining({ itemName: "اسم محلي", itemNumber: "1087" }));
  });
});

describe("item client", () => {
  it("sends master edits to the branch API without SQL credentials", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ rowVersion: "v", changeVersion: 1 }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const session: DeviceSession = {
      deviceId: "device",
      businessId: "business",
      branchId: "branch",
      stationId: "station",
      deviceName: "صندوق",
      deviceType: "POS",
      deviceToken: "token",
      registeredAt: "2026-10-07T00:00:00Z",
    };
    const api = createItemClient(session);
    await api.saveMaster("item-1", {
      itemName: "اسم",
      isActive: true,
      isProhibited: false,
      isPublishedOnline: false,
      rowVersion: "v",
    });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/items/item-1");
    expect(init.method).toBe("PUT");
    const body = String(init.body);
    expect(body).not.toMatch(/password|connectionString|Initial Catalog|Server=/i);
    vi.unstubAllGlobals();
  });
});
