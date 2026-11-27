import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
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
  it("explains an empty run: counted items without sales are not short", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url === "/api/shortages/options") {
        return reply({ branches: [{ branchId: "br", branchName: "Labtop", current: false, lastPushAt: null, stale: false }], customInventories: [], users: [], draft: null, pageCount: 12 });
      }
      if (url === "/api/shortages/runs") {
        return reply({ resumed: false, draft: { ...runHeader, itemCount: 0 }, summary: { scopeItems: 46, shortItems: 0, noSalesItems: 46, coveredItems: 0, branches: ["Labtop"] } });
      }
      if (url === "/api/shortages/runs/r1") return reply({ run: { ...runHeader, itemCount: 0 }, items: [] });
      return reply({});
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.Process"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^معالجة النواقص/ }));
    await userEvent.click(await screen.findByRole("button", { name: /^Labtop/ }));
    await userEvent.click(screen.getByRole("button", { name: "متابعة" }));
    await userEvent.click(await screen.findByRole("button", { name: "الأصناف المهمة" }));
    await userEvent.click(screen.getByRole("button", { name: "تثبيت ومتابعة" }));
    expect(await screen.findByText("في النطاق 46 صنفاً لـ Labtop، ولم يظهر منها نقص:")).toBeTruthy();
    expect(screen.getByText(/46 صنف بلا مبيعات على المركز/)).toBeTruthy();
  });

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
    // تسلسل النظام القديم: اختيار الفروع ← نوع معالجة النواقص ← عدد أيام التوفير (− N +) ← تثبيت ومتابعة.
    await userEvent.click(await screen.findByRole("button", { name: "متابعة" }));
    expect(await screen.findByText("يرجى اختيار فرع واحد على الأقل")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /^فرع نابلس/ }));
    await userEvent.click(screen.getByRole("button", { name: "متابعة" }));
    await userEvent.click(await screen.findByRole("button", { name: "الأصناف المهمة" }));
    expect(screen.getByText("هذه القيمة تُستخدم فقط لهذا الحساب ولا تُحفظ في الإعدادات.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "إنقاص عدد الأيام" }));
    expect(screen.getByLabelText("عدد أيام التوفير", { selector: "output" }).textContent).toBe("6");
    await userEvent.click(screen.getByRole("button", { name: "زيادة عدد الأيام" }));
    await userEvent.click(screen.getByRole("button", { name: "تثبيت ومتابعة" }));
    await vi.waitFor(() => expect(calls.find((call) => call.url === "/api/shortages/runs")?.body).toMatchObject({ shortageType: "Important", branchIds: ["br"], supplyDays: 7 }));

    const row = (await screen.findByText("فحم سريع")).closest("article") as HTMLElement;
    expect(within(row).getByText(/مطلوب شراء 4/)).toBeTruthy();
    expect(within(row).getByText(/سرعة البيع غير محسوبة/)).toBeTruthy();

    await userEvent.click(within(row).getByRole("button", { name: "الكميات" }));
    expect((within(row).getByRole("textbox", { name: "شراء 650" }) as HTMLInputElement).value).toBe("4");
    await userEvent.click(within(row).getByRole("button", { name: "حفظ" }));
    expect(calls.find((call) => call.method === "PUT")?.body).toMatchObject({ purchaseQuantity: 4, balanceQuantity: 6, delete: false });
    await userEvent.click(screen.getByRole("tab", { name: "مسؤول الشراء (1)" }));
    expect(await screen.findByText(/القرار: شراء 4/)).toBeTruthy();
  });
});

describe("follow-up", () => {
  it("shows the open follow-up count on the menu button, as in the legacy app", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url === "/api/shortages/tasks/open-count") return reply({ open: 333 });
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      return reply({});
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.FollowUp"]} onBack={() => undefined} />);
    expect(await screen.findByRole("button", { name: "متابعة النواقص (333)" })).toBeTruthy();
  });

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
    // كما في النظام القديم: «هل استلمت هذا الصنف؟» نعم/لا، ثم «تثبيت وإرسال الرد» بتأكيد.
    const question = await screen.findByRole("group", { name: "هل استلمت 650؟" });
    expect(screen.queryByText(/المتوقع/)).toBeNull();
    expect(screen.getByText("تعرض الشاشة تقاريرك فقط.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "تثبيت وإرسال الرد" }));
    expect(await screen.findByText(/أجب عن كل الأصناف بنعم أو لا/)).toBeTruthy();
    await userEvent.click(within(question).getByRole("button", { name: "نعم، استلمته" }));
    expect(within(question).getByRole("button", { name: "نعم، استلمته" }).className).toContain("is-selected");
    await userEvent.click(screen.getByRole("button", { name: "تثبيت وإرسال الرد" }));
    const dialog = await screen.findByRole("dialog", { name: "تثبيت وإرسال الرد" });
    expect(within(dialog).getByText(/لن تتمكن من تعديلها/)).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("button", { name: "تثبيت وإرسال الرد" }));
    await vi.waitFor(() => expect(calls.find((call) => call.url === "/api/shortages/audits/q1/answer")?.body).toEqual({ items: [{ requestItemId: "x1", received: true }] }));
    expect(await screen.findByText("تم إرسال ردك بنجاح.")).toBeTruthy();
  });

  it("creates an audit on processed shortage tasks: date, employee, branch, recipient, then items with one of each type", async () => {
    const calls: { method: string; url: string; body?: unknown }[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push({ method: init?.method ?? "GET", url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/shortages/audits/candidates")) {
        return reply({ date: "2026-11-24", employees: [{ name: "سلام", items: [
          { taskId: "t1", itemId: "i1", itemNumber: "650", itemName: "فحم سريع", taskType: "Purchase", quantity: 4 },
          { taskId: "t2", itemId: "i2", itemNumber: "224", itemName: "رأس", taskType: "Warehouse", quantity: 2 },
        ] }] });
      }
      if (url === "/api/shortages/audits/options") return reply({ branches: [{ branchId: "br", branchName: "فرع نابلس", current: true }], users: [] });
      if (url === "/api/shortages/audits/options?branchId=br") return reply({ branches: [], users: [{ userId: "u1", displayName: "أحمد", employeeNumber: null }] });
      if (url === "/api/shortages/audits") return reply({ requestId: "q9" });
      return reply({});
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.Audit"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^التدقيق/ }));
    await userEvent.click(await screen.findByRole("button", { name: "متابعة" }));
    await userEvent.click(await screen.findByRole("button", { name: /سلام/ }));
    await userEvent.click(await screen.findByRole("button", { name: /فرع نابلس/ }));
    await userEvent.click(await screen.findByRole("button", { name: /أحمد/ }));
    expect(await screen.findByText("مسؤول الشراء")).toBeTruthy();
    expect(screen.getByText("المخزن")).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "تحديد 650" }));
    await userEvent.click(screen.getByRole("button", { name: "إنشاء طلب التدقيق والمتابعة" }));
    expect(await screen.findByText("يجب اختيار صنف واحد على الأقل من كل نوع معالجة متاح.")).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "تحديد 224" }));
    await userEvent.click(screen.getByRole("button", { name: "إنشاء طلب التدقيق والمتابعة" }));
    await vi.waitFor(() => expect(calls.find((call) => call.method === "POST" && call.url === "/api/shortages/audits")?.body)
      .toMatchObject({ processedByName: "سلام", branchId: "br", recipientUserId: "u1", taskIds: ["t1", "t2"] }));
    expect(await screen.findByText("تم إنشاء طلب التدقيق.")).toBeTruthy();
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
    await userEvent.click(within(await screen.findByRole("group", { name: "تم اختيار الفرع" })).getByRole("button", { name: "متابعة" }));
    await userEvent.click(await screen.findByRole("button", { name: /^جرد يدوي/ }));
    await userEvent.click(await screen.findByRole("button", { name: /^الجرد المخصص/ }));
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
    // تسلسل النظام القديم: اختيار الفروع ← التالي ← اختيار الأصناف ← تثبيت ← اسم الجرد.
    const branchStep = await screen.findByRole("region", { name: "اختيار الفروع" });
    expect((within(branchStep).getByRole("button", { name: "التالي" }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(await within(branchStep).findByRole("button", { name: /^فرع نابلس/ }));
    expect(within(branchStep).getByRole("button", { name: /^فرع نابلس/ }).className).toContain("is-selected");
    expect(within(branchStep).getByRole("button", { name: /^فرع نابلس/ }).getAttribute("aria-pressed")).toBe("true");
    await userEvent.click(within(branchStep).getByRole("button", { name: "التالي" }));

    await userEvent.type(await screen.findByRole("textbox", { name: "اكتب اسم أو رقم الصنف" }), "فحم");
    await userEvent.click(screen.getByRole("button", { name: "بحث" }));
    expect(await screen.findByText(/صفحة 1 من 2/)).toBeTruthy();
    expect(screen.getByText("تم تحديد 0 صنفاً")).toBeTruthy();

    await userEvent.click(screen.getByRole("button", { name: "تحديد الكل" }));
    expect(screen.getByText("تم تحديد 12 صنفاً")).toBeTruthy();
    const first = screen.getByRole("checkbox", { name: "تحديد 650" });
    expect((first as HTMLInputElement).checked).toBe(true);
    expect(first.closest("label")?.className).toContain("is-selected");
    await userEvent.click(screen.getByRole("button", { name: "إلغاء تحديد الكل" }));
    expect(screen.getByText("تم تحديد 0 صنفاً")).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "تحديد 651" }));
    expect(screen.getByRole("checkbox", { name: "تحديد 651" }).closest("label")?.className).toContain("is-selected");

    await userEvent.click(screen.getByRole("button", { name: "تثبيت" }));
    const name = await screen.findByRole("textbox", { name: "اسم الجرد" });
    expect(name.getAttribute("placeholder")).toBe("مثال: نكهات Lume");
    await userEvent.type(name, "جرد الفحم");
    await userEvent.click(screen.getByRole("button", { name: "تثبيت الجرد" }));
    await vi.waitFor(() => expect(calls.find((call) => call.method === "POST" && call.url === "/api/shortages/custom-inventories")?.body)
      .toMatchObject({ name: "جرد الفحم", branchIds: ["br"], itemCodes: ["651"] }));

    // «الجرود المحددة»: توقيف أو حذف (المستعمل يُوقف ويختفي).
    await userEvent.click(await screen.findByRole("button", { name: "الجرود المحددة (1)" }));
    const table = screen.getByRole("table", { name: "الجرود المخصصة" });
    await userEvent.click(within(table).getByRole("button", { name: "حذف" }));
    const dialog = await screen.findByRole("dialog", { name: "حذف الجرد المخصص" });
    expect(within(dialog).getByText(/سيُوقف ويختفي/)).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("button", { name: "حذف" }));
    await vi.waitFor(() => expect(calls.some((call) => call.url === "/api/shortages/custom-inventories/c1/delete")).toBe(true));
  });
});

describe("manual shortages", () => {
  it("defaults to yesterday..today and lists only the employees who did a manual count in the period", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      urls.push(url);
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url === "/api/shortages/options") {
        return reply({
          branches: [{ branchId: "br", branchName: "فرع نابلس", current: true, lastPushAt: "2026-11-24T05:59:00Z", stale: false }],
          customInventories: [], draft: null, pageCount: 12,
          users: [{ userId: "u1", displayName: "سامر" }, { userId: "u2", displayName: "رنا" }],
        });
      }
      if (url.startsWith("/api/shortages/manual-counters?")) {
        const params = new URL(url, "http://x").searchParams;
        return reply({ from: params.get("from"), to: params.get("to"), users: params.get("from") === "2026-01-01" ? [] : [{ userId: "u2", displayName: "رنا", employeeNumber: null, sessions: 2 }] });
      }
      return reply({});
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Shortages.Process"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^معالجة النواقص/ }));
    await userEvent.click(await screen.findByRole("checkbox", { name: "فرع نابلس" }));
    await userEvent.click(screen.getByRole("button", { name: "متابعة" }));
    await userEvent.click(await screen.findByRole("button", { name: "الجرد اليدوي" }));
    await userEvent.click(await screen.findByRole("button", { name: /^جرد يدوي/ }));

    const from = screen.getByLabelText("من تاريخ") as HTMLInputElement;
    const to = screen.getByLabelText("إلى تاريخ") as HTMLInputElement;
    const today = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Hebron", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
    expect(to.value).toBe(today);
    expect(new Date(`${today}T00:00:00Z`).getTime() - new Date(`${from.value}T00:00:00Z`).getTime()).toBe(86_400_000);

    expect(await screen.findByRole("checkbox", { name: "رنا" })).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: "سامر" })).toBeNull();
    expect(urls.some((url) => url.startsWith("/api/shortages/manual-counters?") && url.includes("branchIds=br"))).toBe(true);

    fireEvent.change(from, { target: { value: "2026-01-01" } });
    expect(await screen.findByText("لا يوجد موظف نفّذ جرداً يدوياً في هذه الفترة.")).toBeTruthy();
  });
});
