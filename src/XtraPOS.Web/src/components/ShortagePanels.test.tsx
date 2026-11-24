import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { correctionDifference, parseItemCodes, parseOptionalQuantity } from "../inventory/inventoryRules";
import type { DeviceSession } from "../types/setup";
import { InventoryScreen } from "./InventoryScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const device = {
  deviceId: "d1", businessId: "b", branchId: "br", stationId: "s", deviceName: "صندوق 1", deviceType: "POS", deviceToken: "token",
  registeredAt: "2026-01-01", businessName: "المتجر", branchName: "فرع نابلس", branchType: "Retail", stationName: "كاونتر 1", stationNumber: 1,
} as DeviceSession;

const reply = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: async () => body });

const configuration = {
  businessDate: "2026-11-24", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: null, importantItems: 3, pageItems: [],
  customInventories: [{ customInventoryId: "c1", name: "أراجيل العرض", items: 4 }],
};

const runHeader = {
  runId: "r1", shortageType: "Important", shortageTypeLabel: "نواقص الأصناف المهمة", supplyDays: 7, pages: [], status: "Draft",
  createdByName: "المشرف", createdAt: "2026-11-24T06:00:00Z", lastSavedAt: "2026-11-24T06:00:00Z", completedAt: null,
  itemCount: 1, processedCount: 0, mine: true, customInventoryName: null, manualFrom: null, manualTo: null,
  branches: [{ branchId: "br", branchName: "فرع نابلس", snapshotAt: "2026-11-24T05:59:00Z", stale: false }],
};

const runItem = {
  itemId: "i1", itemNumber: "650", itemName: "فحم سريع", totalNeed: 10, totalSurplus: 6, requiredQuantity: 4, balanceQuantity: 6,
  missingRate: true, missingQuantity: false, isProcessed: false, purchaseQuantity: null, warehouseQuantity: null, balanceDecision: null,
  deleteRequested: false, alreadySupplied: false, note: null, processedByName: null,
  branches: [{ branchId: "br", branchName: "فرع نابلس", currentQuantity: 4, dailySalesRate: 2, target: 14, need: 10, surplus: 0 }],
};

describe("shortage processing", () => {
  it("calculates, saves a decision per item and refuses to finalize while items are open", async () => {
    const calls: { method: string; url: string; body?: unknown }[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push({ method: init?.method ?? "GET", url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url === "/api/shortages/options") {
        return reply({ branches: [{ branchId: "br", branchName: "فرع نابلس", current: true, lastPushAt: "2026-11-24T05:59:00Z", stale: false }], customInventories: [], users: [], draft: null, pageCount: 12 });
      }
      if (url === "/api/shortages/runs") return reply({ resumed: false, draft: runHeader });
      if (url === "/api/shortages/runs/r1") return reply({ run: runHeader, items: [runItem] });
      if (url === "/api/shortages/items/i1/branches") return reply({ itemId: "i1", branches: [] });
      if (url === "/api/shortages/runs/r1/items/i1") return reply({ saved: true });
      return reply({});
    }));

    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.Process"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^معالجة النواقص/ }));
    await userEvent.click(await screen.findByRole("button", { name: "تثبيت ومتابعة" }));
    expect(calls.find((call) => call.url === "/api/shortages/runs")?.body).toMatchObject({ shortageType: "Important", branchIds: ["br"], supplyDays: 7 });

    const row = (await screen.findByText("فحم سريع")).closest("article") as HTMLElement;
    expect(within(row).getByText(/مطلوب شراء 4/)).toBeTruthy();
    expect(within(row).getByText(/سرعة البيع غير محسوبة/)).toBeTruthy();

    await userEvent.click(within(row).getByRole("button", { name: "الكميات" }));
    expect((within(row).getByRole("textbox", { name: "شراء 650" }) as HTMLInputElement).value).toBe("4");
    await userEvent.click(within(row).getByRole("button", { name: "حفظ" }));
    expect(calls.find((call) => call.method === "PUT")?.body).toMatchObject({ purchaseQuantity: 4, balanceQuantity: 6, delete: false });
    await userEvent.click(screen.getByRole("tab", { name: "المعالج" }));
    expect(await screen.findByText(/القرار: شراء 4/)).toBeTruthy();
  });
});

describe("follow-up", () => {
  const view = (tab: string) => ({
    tab, from: "2026-11-10", to: "2026-11-24", counts: { purchase: 0, warehouse: 0, balance: 0, delete: 1, done: 0 },
    rows: tab === "Delete" ? [{
      taskId: "t1", itemId: "i1", itemNumber: "650", itemName: "فحم سريع", taskType: "Delete", quantity: null, note: null, status: "Open",
      assignedAt: "2026-11-24T06:00:00Z", assignedByName: "المشرف", approvalStatus: "Pending", decidedByName: null, completedAt: null, completedByName: null,
    }] : [],
  });

  function stub() {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/shortages/tasks?")) return reply(view(new URL(url, "http://x").searchParams.get("tab") ?? "Purchase"));
      if (url === "/api/shortages/suppliers") return reply({ suppliers: [] });
      return reply({});
    }));
  }

  it("lets only the manager approve a delete", async () => {
    stub();
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.FollowUp"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^متابعة النواقص/ }));
    await userEvent.click(await screen.findByRole("tab", { name: /مطلوب حذف/ }));
    expect(await screen.findByText("بانتظار موافقة المدير")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "اعتماد الحذف" })).toBeNull();
    cleanup();

    stub();
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.FollowUp", "Shortages.Approve"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^متابعة النواقص/ }));
    await userEvent.click(await screen.findByRole("tab", { name: /مطلوب حذف/ }));
    expect(await screen.findByRole("button", { name: "اعتماد الحذف" })).toBeTruthy();
  });
});

describe("receipt audits", () => {
  it("lets the recipient answer without seeing the expected quantities", async () => {
    const calls: { url: string; body?: unknown }[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/shortages/audits/mine")) {
        return reply({ requests: [{
          requestId: "q1", branchName: "فرع نابلس", recipientUserId: "u1", recipientName: "أحمد", note: null, status: "Pending", expired: false,
          createdByName: "المدير", createdAt: "2026-11-24T06:00:00Z", expiresAt: "2026-12-01T06:00:00Z", answeredAt: null, score: null,
          items: [{ requestItemId: "x1", itemNumber: "650", itemName: "فحم سريع", expectedQuantity: null, receivedQuantity: null }],
        }] });
      }
      if (url.startsWith("/api/shortages/efficiency")) return reply({ from: "2026-10-26", to: "2026-11-24", ownOnly: true, employees: [] });
      if (url === "/api/shortages/audits/q1/answer") return reply({ score: 10 });
      return reply({});
    }));

    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.Efficiency"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^تقرير الكفاءة/ }));
    const input = await screen.findByRole("textbox", { name: "الكمية المستلمة 650" });
    expect(screen.queryByText(/المتوقع/)).toBeNull();
    expect(screen.getByText("تعرض الشاشة تقاريرك فقط.")).toBeTruthy();
    await userEvent.type(input, "5");
    await userEvent.click(screen.getByRole("button", { name: "إرسال الرد" }));
    expect(calls.find((call) => call.url === "/api/shortages/audits/q1/answer")?.body).toEqual({ items: [{ requestItemId: "x1", receivedQuantity: 5 }] });
    expect(await screen.findByText("تم الرد. درجتك 10 من 10.")).toBeTruthy();
  });
});

describe("custom count", () => {
  it("opens a custom count from manual options by its name", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push(`${url} ${typeof init?.body === "string" ? init.body : ""}`);
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: null });
      return reply({ session: { sessionId: "s1", inventoryType: "Custom", inventoryTypeLabel: "جرد مخصص", status: "Open", businessDate: "2026-11-24", startedAt: "", lastSavedAt: "", totalItems: 4, countedItems: 0, postedItems: 0, pages: [] }, items: [] });
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^الجرد/ }));
    await userEvent.click(await screen.findByRole("button", { name: /فرع نابلس/ }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "تأكيد الفرع" })).getByRole("button", { name: "نعم، متابعة" }));
    await userEvent.click(await screen.findByRole("button", { name: /^جرد يدوي/ }));
    await userEvent.click(await screen.findByRole("button", { name: /^جرد مخصص/ }));
    await userEvent.click(await screen.findByRole("button", { name: /أراجيل العرض/ }));
    await vi.waitFor(() => expect(calls.some((call) => call.startsWith("/api/inventory/sessions ") && call.includes('"inventoryType":"Custom"') && call.includes('"customInventoryId":"c1"'))).toBe(true));
  });
});

describe("shortage rules", () => {
  it("parses pasted item numbers and optional quantities", () => {
    expect(parseItemCodes("650\n١٣٠٠، 650 224")).toEqual(["650", "1300", "224"]);
    expect(parseOptionalQuantity("")).toBeNull();
    expect(parseOptionalQuantity("٣")).toBe(3);
    expect(parseOptionalQuantity("-2")).toBeUndefined();
  });
});

describe("remote correction by difference", () => {
  it("sends the difference: expected 7, counted 5 → −2, so a branch at 6 ends at 4", () => {
    expect(correctionDifference(7, 5)).toBe(-2);
    expect(6 + (correctionDifference(7, 5) ?? 0)).toBe(4);
    expect(correctionDifference(2.5, 4)).toBe(1.5);
  });

  it("has no difference when the branch balance is unknown", () => {
    expect(correctionDifference(null, 5)).toBeNull();
    expect(correctionDifference(7, null)).toBeNull();
  });
});

describe("define inventory", () => {
  it("searches by name or number, selects all or none, highlights picks, and saves the chosen items", async () => {
    const calls: { method: string; url: string; body?: unknown }[] = [];
    const used = { customInventoryId: "c1", name: "أراجيل العرض", isActive: true, createdByName: "المدير", createdAt: "2026-11-24T06:00:00Z", branchIds: ["br"], branchNames: ["فرع نابلس"], used: true, items: [] };
    const view = { customInventories: [used], branches: [{ branchId: "br", branchName: "فرع نابلس", current: true }] };
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push({ method: init?.method ?? "GET", url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/shortages/custom-inventories/items")) {
        return reply({ truncated: false, items: Array.from({ length: 12 }, (_, index) => ({ itemId: `i${index}`, itemNumber: `65${index}`, itemName: `فحم ${index}` })) });
      }
      if (url === "/api/shortages/custom-inventories/c1/delete") return reply({ deleted: false, stopped: true, view: { ...view, customInventories: [] } });
      if (url === "/api/shortages/custom-inventories") return reply(view);
      return reply({});
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Inventory.Define"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^تحديد جرد/ }));
    await userEvent.type(await screen.findByRole("textbox", { name: "اكتب اسم أو رقم الصنف" }), "فحم");
    await userEvent.click(screen.getByRole("button", { name: "بحث" }));
    expect(await screen.findByText(/صفحة 1 من 2/)).toBeTruthy();

    await userEvent.click(screen.getByRole("button", { name: "تحديد الكل (12)" }));
    expect(screen.getByText("أصناف الجرد (12)")).toBeTruthy();
    const first = screen.getByRole("checkbox", { name: "تحديد 650" });
    expect((first as HTMLInputElement).checked).toBe(true);
    expect(first.closest("label")?.className).toContain("is-selected");
    await userEvent.click(screen.getByRole("button", { name: "إلغاء الكل" }));
    expect(screen.getByText("أصناف الجرد (0)")).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "تحديد 651" }));
    expect(screen.getByRole("checkbox", { name: "تحديد 651" }).closest("label")?.className).toContain("is-selected");

    await userEvent.type(screen.getByRole("textbox", { name: "اسم الجرد المخصص" }), "جرد الفحم");
    await userEvent.click(screen.getByRole("checkbox", { name: "فرع نابلس" }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await vi.waitFor(() => expect(calls.find((call) => call.method === "POST" && call.url === "/api/shortages/custom-inventories")?.body)
      .toMatchObject({ name: "جرد الفحم", branchIds: ["br"], itemCodes: ["651"] }));

    const table = screen.getByRole("table", { name: "الجرود المخصصة" });
    await userEvent.click(within(table).getByRole("button", { name: "حذف" }));
    const dialog = await screen.findByRole("dialog", { name: "حذف الجرد المخصص" });
    expect(within(dialog).getByText(/سيُوقف ويختفي/)).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("button", { name: "حذف" }));
    await vi.waitFor(() => expect(calls.some((call) => call.url === "/api/shortages/custom-inventories/c1/delete")).toBe(true));
  });
});
