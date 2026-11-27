import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { InventoryItem, InventorySession } from "../api/inventory";
import {
  filterItems,
  formatDifference,
  inventoryMenu,
  parseCountInput,
  stepQuantity,
  togglePage,
} from "../inventory/inventoryRules";
import type { DeviceSession } from "../types/setup";
import { InventoryScreen } from "./InventoryScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

const session = {
  deviceId: "d1", businessId: "b", branchId: "br", stationId: "s", deviceName: "صندوق 1", deviceType: "POS", deviceToken: "token",
  registeredAt: "2026-01-01", businessName: "المتجر", branchName: "فرع نابلس", branchType: "Retail", stationName: "كاونتر 1", stationNumber: 1,
} as DeviceSession;

const reply = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: async () => body });

const openSession: InventorySession = {
  sessionId: "s1", inventoryType: "Important", inventoryTypeLabel: "جرد الأصناف المهمة", status: "Open", businessDate: "2026-11-23",
  startedAt: "2026-11-23T08:00:00Z", lastSavedAt: "2026-11-23T08:00:00Z", completedAt: null,
  totalItems: 2, countedItems: 1, uncountedItems: 1, postedItems: 0, pages: [],
};

function item(overrides: Partial<InventoryItem>): InventoryItem {
  return {
    sessionItemId: "l1", itemId: "i1", itemNumber: "650", itemName: "فحم سريع", isCounted: false, countedQuantity: null,
    countedAt: null, countedByName: null, posted: false, expectedQuantity: null, difference: null, quantityAfterPosting: null,
    ...overrides,
  };
}

function stubApi(items: InventoryItem[]) {
  vi.stubGlobal("fetch", vi.fn((url: string) => {
    if (url.startsWith("/api/inventory/configuration")) {
      return reply({ businessDate: "2026-11-23", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: 7, importantItems: 2, pageItems: [] });
    }
    if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: null });
    if (url === "/api/inventory/sessions") return reply({ session: openSession, resumed: false });
    if (url.includes("/items")) return reply({ session: openSession, items });
    return reply({});
  }));
}

/** تسلسل النظام القديم: الجرد ← الفرع ← «تم اختيار الفرع» (متابعة) ← الأصناف المهمة ← «تم اختيار نوع الجرد» (جديد). */
async function openImportantCount() {
  await userEvent.click(screen.getByRole("button", { name: /^الجرد/ }));
  await userEvent.click(await screen.findByRole("button", { name: /فرع نابلس/ }));
  const chosen = await screen.findByRole("group", { name: "تم اختيار الفرع" });
  expect(within(chosen).getByText("فرع نابلس")).toBeTruthy();
  await userEvent.click(within(chosen).getByRole("button", { name: "متابعة" }));
  await userEvent.click(await screen.findByRole("button", { name: /جرد الأصناف المهمة/ }));
  const card = await screen.findByRole("group", { name: "تم اختيار نوع الجرد" });
  await vi.waitFor(() => expect(within(card).getByText("لا يوجد جرد مفتوح اليوم")).toBeTruthy());
  await userEvent.click(within(card).getByRole("button", { name: "جديد" }));
}

describe("InventoryScreen", () => {
  it("follows the legacy sequence: branch card, type card with continue/new/change branch, pages with the same actions, and custom with branch states", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${url} ${typeof init?.body === "string" ? init.body : ""}`);
      if (url.startsWith("/api/inventory/configuration")) {
        return reply({
          businessDate: "2026-11-23", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: 7, importantItems: 2,
          pageItems: [{ page: 7, items: 3 }, { page: 8, items: 2 }],
          customInventories: [
            { customInventoryId: "c1", name: "جرد الفحم", items: 4, branches: [{ branchName: "فرع نابلس", completed: true }, { branchName: "فرع رام الله", completed: false }] },
            { customInventoryId: "c2", name: "جرد الفحم", items: 4 },
          ],
        });
      }
      if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: openSession });
      if (url === "/api/inventory/sessions") return reply({ session: { ...openSession, inventoryType: "Custom" }, resumed: false });
      if (url.includes("/items")) return reply({ session: openSession, items: [] });
      return reply({});
    }));
    render(<InventoryScreen session={session} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^الجرد/ }));
    await userEvent.click(await screen.findByRole("button", { name: /فرع نابلس/ }));
    // «تم اختيار الفرع»: بطاقة فيها «متابعة» فقط، و«رجوع» يعيد لقائمة الفروع مع تظليل الفرع المختار.
    const chosen = await screen.findByRole("group", { name: "تم اختيار الفرع" });
    expect(within(chosen).getAllByRole("button").map((button) => button.textContent)).toEqual(["متابعة"]);
    await userEvent.click(screen.getByRole("button", { name: "رجوع" }));
    expect(screen.getByRole("button", { name: /فرع نابلس/ }).className).toContain("is-selected");
    await userEvent.click(screen.getByRole("button", { name: /فرع نابلس/ }));
    await userEvent.click(within(await screen.findByRole("group", { name: "تم اختيار الفرع" })).getByRole("button", { name: "متابعة" }));

    // الأصناف المهمة: «تم اختيار نوع الجرد» مع الفرع، ومتابعة / جديد / تغيير الفرع.
    await userEvent.click(await screen.findByRole("button", { name: /جرد الأصناف المهمة/ }));
    const card = await screen.findByRole("group", { name: "تم اختيار نوع الجرد" });
    expect(within(card).getByText("فرع نابلس")).toBeTruthy();
    await vi.waitFor(() => expect((within(card).getByRole("button", { name: "متابعة" }) as HTMLButtonElement).disabled).toBe(false));
    expect(within(card).getByText(/تم جرد 1 من 2/)).toBeTruthy();
    expect(within(card).getByRole("button", { name: "جديد" })).toBeTruthy();
    await userEvent.click(within(card).getByRole("button", { name: "تغيير الفرع" }));
    expect(await screen.findByRole("button", { name: /فرع نابلس/ })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /فرع نابلس/ }));
    await userEvent.click(within(await screen.findByRole("group", { name: "تم اختيار الفرع" })).getByRole("button", { name: "متابعة" }));

    // الدوري: صفحة اليوم عليها ✓، وتحت الصفحات متابعة / جديد / تغيير الفرع.
    await userEvent.click(await screen.findByRole("button", { name: /الجرد الدوري/ }));
    const today = await screen.findByRole("button", { name: /الصفحة 7/ });
    expect(today.textContent).toContain("✓");
    expect(screen.getByRole("button", { name: /الصفحة 8/ }).textContent).not.toContain("✓");
    expect(screen.getByRole("button", { name: "جديد" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "تغيير الفرع" })).toBeTruthy();
    await vi.waitFor(() => expect(calls.some((call) => call.startsWith("GET /api/inventory/sessions/open?") && call.includes("Periodic"))).toBe(true));
    await userEvent.click(screen.getByRole("button", { name: "رجوع" }));

    // اليدوي: جرد يدوي أو الجرد المخصص، وتحت كل اسم حالة كل فرع؛ لا يتكرر الاسم.
    await userEvent.click(await screen.findByRole("button", { name: /^جرد يدوي/ }));
    expect(await screen.findByRole("button", { name: /^جرد يدوي/ })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /^الجرد المخصص/ }));
    const named = await screen.findAllByRole("button", { name: /جرد الفحم/ });
    expect(named).toHaveLength(1);
    expect(named[0].textContent).toContain("فرع نابلس (مكتمل)");
    expect(named[0].textContent).toContain("فرع رام الله (غير مكتمل)");
    await userEvent.click(named[0]);
    await vi.waitFor(() => expect(calls.some((call) => call.startsWith("POST /api/inventory/sessions ") && call.includes("c1"))).toBe(true));
  });

  it("keeps the count blind for a cashier even if a quantity reaches the screen", async () => {
    stubApi([
      item({ itemId: "i1", itemNumber: "650", isCounted: true, countedQuantity: 8, expectedQuantity: 10, difference: -2 }),
      item({ itemId: "i2", itemNumber: "224", itemName: "رأس أرجيلة" }),
    ]);
    render(<InventoryScreen session={session} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await openImportantCount();
    expect(await screen.findByText("رأس أرجيلة")).toBeTruthy();
    await userEvent.click(screen.getByRole("tab", { name: /^تم الجرد/ }));
    const row = (await screen.findByText("فحم سريع")).closest("article") as HTMLElement;
    expect(within(row).getByText("المعدود: 8")).toBeTruthy();
    expect(within(row).queryByText(/المفترض/)).toBeNull();
    expect(within(row).queryByText(/الفارق/)).toBeNull();
  });

  it("shows the expected quantity and difference to a supervisor", async () => {
    stubApi([item({ itemId: "i1", isCounted: true, countedQuantity: 8, expectedQuantity: 10, difference: -2 })]);
    render(<InventoryScreen session={session} permissions={["Inventory.Count", "Inventory.Review"]} onBack={() => undefined} />);
    await openImportantCount();
    await userEvent.click(await screen.findByRole("tab", { name: /^تم الجرد/ }));
    const row = (await screen.findByText("فحم سريع")).closest("article") as HTMLElement;
    expect(within(row).getByText("المفترض: 10")).toBeTruthy();
    expect(within(row).getByText("الفارق: −2")).toBeTruthy();
  });
});

describe("inventory rules", () => {
  it("shows each role only the cards it may open, in the published order", () => {
    expect(inventoryMenu(["Inventory.Count", "Shortages.Efficiency"]).map((card) => card.label)).toEqual(["الجرد", "تقرير الكفاءة"]);
    const admin = inventoryMenu(["System.Administer"]);
    expect(admin.map((card) => card.label)).toEqual([
      "الجرد", "تحديد جرد", "معالجة النواقص", "متابعة النواقص", "معالجة الأصناف",
      "تقرير الجرد", "التدقيق", "تقرير الكفاءة", "تقرير معالجة النواقص",
    ]);
    expect(admin.filter((card) => card.available).map((card) => card.id)).toEqual(
      ["count", "define", "shortages", "followUp", "items", "report", "audit", "efficiency", "shortageReport"]);
    expect(inventoryMenu(["Inventory.Count", "Inventory.CorrectQuantity"]).map((card) => card.id)).toEqual(["count", "items"]);
  });

  it("accepts zero and Arabic digits but refuses negative or invalid quantities", () => {
    expect(parseCountInput("0")).toBe(0);
    expect(parseCountInput("١٢")).toBe(12);
    expect(parseCountInput("2,5")).toBe(2.5);
    expect(parseCountInput("-1")).toBeNull();
    expect(parseCountInput("abc")).toBeNull();
    expect(parseCountInput("")).toBeNull();
    expect(stepQuantity("", -1)).toBe("0");
    expect(stepQuantity("4", 1)).toBe("5");
  });

  it("filters by tab and by number or name", () => {
    const items = [item({ itemId: "a", itemNumber: "650" }), item({ itemId: "b", itemNumber: "224", itemName: "رأس", isCounted: true })];
    expect(filterItems(items, "current", "").map((value) => value.itemId)).toEqual(["a"]);
    expect(filterItems(items, "counted", "رأس").map((value) => value.itemId)).toEqual(["b"]);
    expect(filterItems(items, "current", "٦٥").map((value) => value.itemId)).toEqual(["a"]);
  });

  it("toggles pages and formats differences", () => {
    expect(togglePage([7], 8)).toEqual([7, 8]);
    expect(togglePage([7, 8], 7)).toEqual([8]);
    expect(formatDifference(-2)).toBe("−2");
    expect(formatDifference(3)).toBe("+3");
    expect(formatDifference(null)).toBe("—");
  });
});

describe("counting another branch", () => {
  it("lists every branch, highlights the chosen one, and sends the branch with each call", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${url} ${typeof init?.body === "string" ? init.body : ""}`);
      if (url === "/api/inventory/branches") {
        return reply({
          notice: null,
          central: true,
          branches: [
            { branchId: "br", branchName: "فرع نابلس", current: true, lastPushAt: null, stale: false },
            { branchId: "b2", branchName: "فرع رام الله", current: false, lastPushAt: "2026-11-24T05:00:00Z", stale: false },
          ],
        });
      }
      if (url.startsWith("/api/inventory/configuration")) {
        return reply({ businessDate: "2026-11-24", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: null, importantItems: 2, pageItems: [] });
      }
      if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: null });
      if (url === "/api/inventory/sessions") return reply({ session: openSession, resumed: false });
      if (url.includes("/items")) return reply({ session: openSession, items: [] });
      return reply({});
    }));
    render(<InventoryScreen session={session} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^الجرد/ }));
    const other = await screen.findByRole("button", { name: /فرع رام الله/ });
    expect(other.textContent).toContain("على المركز");
    expect(other.textContent).not.toContain("أقدم من 30 دقيقة");
    await userEvent.click(other);
    const chosen = await screen.findByRole("group", { name: "تم اختيار الفرع" });
    expect(within(chosen).getByText("فرع رام الله")).toBeTruthy();
    expect(within(chosen).getByText(/يجري على المركز/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "رجوع" }));
    expect(screen.getByRole("button", { name: /فرع رام الله/ }).className).toContain("is-selected");
    expect(screen.getByRole("button", { name: /فرع نابلس/ }).className).not.toContain("is-selected");
    await userEvent.click(screen.getByRole("button", { name: /فرع رام الله/ }));
    await userEvent.click(within(await screen.findByRole("group", { name: "تم اختيار الفرع" })).getByRole("button", { name: "متابعة" }));

    await vi.waitFor(() => expect(calls.some((call) => call.startsWith("GET /api/inventory/configuration?branchId=b2"))).toBe(true));
    await userEvent.click(await screen.findByRole("button", { name: /جرد الأصناف المهمة/ }));
    await vi.waitFor(() => expect(calls.some((call) => call.startsWith("GET /api/inventory/sessions/open?") && call.includes("branchId=b2"))).toBe(true));
    await userEvent.click(within(await screen.findByRole("group", { name: "تم اختيار نوع الجرد" })).getByRole("button", { name: "جديد" }));
    await vi.waitFor(() => expect(calls.some((call) => call.startsWith("POST /api/inventory/sessions ") && call.includes('"branchId":"b2"'))).toBe(true));
    await vi.waitFor(() => expect(calls.some((call) => call.includes("/items?branchId=b2"))).toBe(true));
  });
});

describe("counting rules from the owner's notes", () => {
  it("defaults to a faded zero, moves a saved item to «تم الجرد» at once, saves in the background, and enables posting only when everything reached central", async () => {
    const bodies: unknown[] = [];
    const headers: (string | null)[] = [];
    let counted = 0;
    let release: (() => void) | null = null;
    const lines = [item({ itemId: "i1", itemNumber: "650" }), item({ itemId: "i2", itemNumber: "224", itemName: "رأس أرجيلة" })];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (url.startsWith("/api/inventory/configuration")) {
        return reply({ businessDate: "2026-11-23", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: 7, importantItems: 2, pageItems: [] });
      }
      if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: null });
      const current = { ...openSession, countedItems: counted, uncountedItems: 2 - counted };
      if (url === "/api/inventory/sessions") return reply({ session: current, resumed: false });
      if (url.includes("/items")) return reply({ session: current, items: lines });
      if (url.includes("/counts")) {
        const body = JSON.parse(String(init?.body)) as { items: { itemId: string; quantity: number }[] };
        bodies.push(body);
        headers.push(new Headers(init?.headers).get("X-XtraPOS-Background"));
        const saved = body.items.map((entry) => ({ ...lines.find((line) => line.itemId === entry.itemId)!, isCounted: true, countedQuantity: entry.quantity }));
        const answer = () => {
          counted += saved.length;
          return { ok: true, status: 200, json: async () => ({ session: { ...openSession, countedItems: counted, uncountedItems: 2 - counted }, items: saved, rejected: [] }) };
        };
        // الحفظ الأول يتأخر في «المركز» حتى نطلقه: الشاشة لا تنتظره.
        if (bodies.length === 1) return new Promise((resolve) => { release = () => resolve(answer()); });
        return Promise.resolve(answer());
      }
      return reply({});
    }));
    render(<InventoryScreen session={session} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await openImportantCount();

    const first = await screen.findByRole("textbox", { name: "الكمية الفعلية فحم سريع" });
    expect(first.getAttribute("placeholder")).toBe("0");
    expect((first as HTMLInputElement).value).toBe("");
    expect((screen.getByRole("button", { name: "تثبيت الجرد" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/بقي 2/)).toBeTruthy();

    await userEvent.click(within(first.closest("article") as HTMLElement).getByRole("button", { name: "حفظ" }));
    // ينتقل فوراً إلى «تم الجرد» ولو لم يرد المركز بعد.
    expect(screen.queryByRole("textbox", { name: "الكمية الفعلية فحم سريع" })).toBeNull();
    expect(screen.getByRole("tab", { name: "تم الجرد (1)" })).toBeTruthy();
    expect(screen.getByText(/تم جرد 1 من 2/)).toBeTruthy();
    await vi.waitFor(() => expect(bodies[0]).toEqual({ items: [{ itemId: "i1", quantity: 0 }] }));
    expect(headers[0]).toBe("1");

    // الصنف التالي متاح مباشرة، والحفظ الثاني ينتظر دوره خلف الأول.
    const second = screen.getByRole("textbox", { name: "الكمية الفعلية رأس أرجيلة" });
    await userEvent.type(second, "3");
    await userEvent.click(within(second.closest("article") as HTMLElement).getByRole("button", { name: "حفظ" }));
    expect(screen.getByRole("tab", { name: "تم الجرد (2)" })).toBeTruthy();
    expect((screen.getByRole("button", { name: "تثبيت الجرد" }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByText(/بعد وصول كل الحفظ للمركز/)).toBeNull();
    expect(screen.getByText(/جاري إرسال 2 صنف للمركز/)).toBeTruthy();
    expect(bodies).toHaveLength(1);

    release!();
    await vi.waitFor(() => expect(bodies[1]).toEqual({ items: [{ itemId: "i2", quantity: 3 }] }));
    await vi.waitFor(() => expect((screen.getByRole("button", { name: "تثبيت الجرد" }) as HTMLButtonElement).disabled).toBe(false));
    await userEvent.click(screen.getByRole("tab", { name: "تم الجرد (2)" }));
    expect((await screen.findByLabelText("650 تم حفظ العد")).className).toContain("is-selected");
    expect(window.localStorage.getItem(`xtrapos.inventory.pending.${openSession.sessionId}`)).toBeNull();
  });

  it("enables posting from the local count and sends the queue before finalize", async () => {
    const calls: string[] = [];
    let release: (() => void) | null = null;
    const lines = [item({ itemId: "i1", itemNumber: "650" }), item({ itemId: "i2", itemNumber: "224", itemName: "رأس أرجيلة" })];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${url}`);
      if (url.startsWith("/api/inventory/configuration")) {
        return reply({ businessDate: "2026-11-23", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: 7, importantItems: 2, pageItems: [] });
      }
      if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: null });
      if (url === "/api/inventory/sessions") return reply({ session: openSession, resumed: false });
      if (url.includes("/items")) return reply({ session: openSession, items: lines });
      if (url.includes("/counts")) {
        const body = JSON.parse(String(init?.body)) as { items: { itemId: string }[] };
        const answer = () => reply({
          session: { ...openSession, countedItems: 2, uncountedItems: 0 },
          items: body.items.map((entry) => ({ ...lines.find((line) => line.itemId === entry.itemId)!, isCounted: true, countedQuantity: 1 })),
          rejected: [],
        });
        if (!release) return new Promise((resolve) => { release = () => resolve(answer()); });
        return answer();
      }
      if (url.includes("/finalize")) {
        return reply({ session: { ...openSession, status: "Finalizing", countedItems: 2, uncountedItems: 0 }, posted: 0, remote: true, queued: true });
      }
      return reply({});
    }));
    render(<InventoryScreen session={session} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await openImportantCount();
    const first = await screen.findByRole("textbox", { name: "الكمية الفعلية فحم سريع" });
    await userEvent.click(within(first.closest("article") as HTMLElement).getByRole("button", { name: "حفظ" }));
    const second = screen.getByRole("textbox", { name: "الكمية الفعلية رأس أرجيلة" });
    await userEvent.click(within(second.closest("article") as HTMLElement).getByRole("button", { name: "حفظ" }));
    const post = screen.getByRole("button", { name: "تثبيت الجرد" }) as HTMLButtonElement;
    expect(post.disabled).toBe(false);
    await userEvent.click(post);
    await userEvent.click(screen.getByRole("button", { name: "تثبيت" }));
    expect(await screen.findByText(/جاري إرسال \d+ صنف ثم التثبيت/)).toBeTruthy();
    expect(calls.some((call) => call.includes("/finalize"))).toBe(false);
    release!();
    expect(await screen.findByText("تم التثبيت — تُرحَّل الفروق في الخلفية")).toBeTruthy();
    expect(calls.some((call) => call.startsWith("POST") && call.includes("/finalize"))).toBe(true);
  });

  it("keeps unsent counts on the device and sends them in one request when the count reopens", async () => {
    const lines = [item({ itemId: "i1", itemNumber: "650" }), item({ itemId: "i2", itemNumber: "224", itemName: "رأس أرجيلة" })];
    window.localStorage.setItem(`xtrapos.inventory.pending.${openSession.sessionId}`, JSON.stringify([
      { itemId: "i1", itemNumber: "650", quantity: 4, previous: lines[0], attempts: 0 },
      { itemId: "i2", itemNumber: "224", quantity: 1, previous: lines[1], attempts: 0 },
    ]));
    const bodies: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (url.startsWith("/api/inventory/configuration")) {
        return reply({ businessDate: "2026-11-23", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: 7, importantItems: 2, pageItems: [] });
      }
      if (url.startsWith("/api/inventory/sessions/open")) return reply({ session: null });
      if (url === "/api/inventory/sessions") return reply({ session: openSession, resumed: true });
      if (url.includes("/items")) return reply({ session: openSession, items: lines });
      if (url.includes("/counts")) {
        const body = JSON.parse(String(init?.body)) as { items: { itemId: string; quantity: number }[] };
        bodies.push(body);
        return reply({
          session: { ...openSession, countedItems: 2, uncountedItems: 0 },
          items: body.items.map((entry) => ({ ...lines.find((line) => line.itemId === entry.itemId)!, isCounted: true, countedQuantity: entry.quantity })),
          rejected: [],
        });
      }
      return reply({});
    }));
    render(<InventoryScreen session={session} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    await openImportantCount();
    await vi.waitFor(() => expect(bodies).toEqual([{ items: [{ itemId: "i1", quantity: 4 }, { itemId: "i2", quantity: 1 }] }]));
    expect(await screen.findByRole("tab", { name: "تم الجرد (2)" })).toBeTruthy();
    await vi.waitFor(() => expect((screen.getByRole("button", { name: "تثبيت الجرد" }) as HTMLButtonElement).disabled).toBe(false));
    expect(window.localStorage.getItem(`xtrapos.inventory.pending.${openSession.sessionId}`)).toBeNull();
  });
});
