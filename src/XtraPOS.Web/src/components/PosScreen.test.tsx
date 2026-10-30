import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { interpretFastEntry } from "../cart/fastEntry";
import { rankItemMatches, sortSearchResults } from "../cart/itemSearch";
import type { ItemListItem } from "../catalog/types";
import { DEVICE_TYPE_POS, type CurrentUser, type DeviceSession } from "../types/setup";
import { PosScreen } from "./PosScreen";

const session = {
  deviceId: "device",
  businessId: "business",
  branchId: "branch",
  stationId: "station",
  deviceName: "صندوق",
  deviceType: DEVICE_TYPE_POS,
  deviceToken: "token",
  registeredAt: "2026-10-22",
  businessName: "Smoker World",
  branchName: "فرع سوق الذهب",
  branchType: "Retail",
  stationName: "كاونتر 1",
  stationNumber: 1,
} satisfies DeviceSession;

const user = {
  userId: "cashier",
  username: "cashier",
  displayName: "سليم",
  roles: [],
  permissions: ["Items.View"],
  businessName: "Smoker World",
  branchName: "فرع سوق الذهب",
  stationName: "كاونتر 1",
  deviceName: "صندوق",
} satisfies CurrentUser;

function item(itemNumber: string, itemName = `صنف ${itemNumber}`): ItemListItem {
  return { itemId: `id-${itemNumber}`, itemNumber, itemName, isActive: true, isProhibited: false };
}

const catalog = [
  item("224", "صنف منخفض"),
  item("301", "صنف 301"),
  item("650", "اسم طويل جدًا يظهر في نفس الصف ويلتف داخل عمود الصنف دون نقل السعر والكمية إلى سطر مستقل"),
  item("1300", "معسل فرنسي 1 كيلو"),
  ...Array.from({ length: 14 }, (_, index) => item(`650${index}`, `اسم 650 ${index}`)),
];

describe("fast entry routing", () => {
  it("routes scale values, exact items, and forced item numbers", () => {
    expect(interpretFastEntry("20")).toEqual({ kind: "scale", value: "20" });
    expect(interpretFastEntry("300")).toEqual({ kind: "scale", value: "300" });
    expect(interpretFastEntry("0").kind).toBe("invalid");
    expect(interpretFastEntry("301")).toEqual({ kind: "item", itemNumber: "301" });
    expect(interpretFastEntry("#224")).toEqual({ kind: "item", itemNumber: "224" });
  });

  it("ranks exact, then starts-with, then name contains, and limits the first page", () => {
    const matches = [
      item("1650", "بعيد"),
      item("6501", "يبدأ"),
      item("650", "مطابق"),
      item("9", "فيه 650 بالاسم"),
    ];
    expect(rankItemMatches(matches, "650").map((entry) => entry.itemNumber)).toEqual(["650", "6501", "9", "1650"]);
  });

  it("sorts item numbers as text, names in Arabic, and prices numerically", () => {
    const rows = [
      item("20", "عصير"),
      item("100", "شيبس"),
      item("3", "مياه"),
    ];
    const price: Record<string, number> = { "3": 100, "20": 3, "100": 20 };
    expect(sortSearchResults(rows, "number", "asc", () => null).map((entry) => entry.itemNumber)).toEqual(["100", "20", "3"]);
    expect(sortSearchResults(rows, "name", "asc", () => null).map((entry) => entry.itemName)).toEqual(
      [...rows].sort((left, right) => left.itemName.localeCompare(right.itemName, "ar")).map((entry) => entry.itemName),
    );
    expect(sortSearchResults(rows, "price", "asc", (entry) => price[entry.itemNumber] ?? null).map((entry) => entry.itemNumber)).toEqual(["20", "100", "3"]);
    expect(sortSearchResults(rows, "price", "desc", (entry) => price[entry.itemNumber] ?? null).map((entry) => entry.itemNumber)).toEqual(["3", "100", "20"]);
  });
});

describe("POS cashier screen", () => {
  const calls: string[] = [];

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    calls.length = 0;
  });

  it("keeps suggestions out of fast entry and preserves the draft", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const path = String(url);
      calls.push(`${init?.method ?? "GET"} ${path} ${init?.body ?? ""}`);
      if (path.endsWith("/health")) return new Response("ok");
      if (path.includes("/api/pos/cash-session/current")) return json({ session: null });
      if (path.includes("/api/pricing/variable-value/resolve")) {
        const body = JSON.parse(String(init?.body ?? "{}")) as { enteredScaleValue?: number };
        return json({
          referenceItemId: "id-1300",
          referenceItemNumber: "1300",
          stockOwnerItemId: "owner",
          stockOwnerItemNumber: "1360",
          enteredScaleValue: body.enteredScaleValue ?? 0,
          referenceBasePrice: 180,
          referencePriceSource: "CentralDefault",
          weightKg: 0.1,
          weightGrams: 100,
          stockOwnerConversionFactor: 4,
          projectedStockOwnerQuantity: 0.4,
          variableValueDiscountPercent: 0,
          rawDiscountedValue: body.enteredScaleValue ?? 0,
          finalSellingPrice: body.enteredScaleValue ?? 0,
          roundingPolicy: "LegacyVariableValueV1",
          projectedCost: null,
          costSource: "Unavailable",
          branchId: "branch",
        });
      }
      if (path.includes("/api/pricing/items/")) {
        const itemId = path.split("/api/pricing/items/")[1]?.split("/")[0] ?? "missing";
        return json({
          itemId,
          priceTypeId: "price",
          priceTypeCode: "Price0",
          resolvedPrice: 15,
          priceSource: "CentralDefault",
        });
      }
      if (path.includes("/api/items")) {
        const search = new URL(path, "http://localhost").searchParams.get("search") ?? "";
        const items = catalog.filter((entry) => entry.itemNumber.includes(search) || entry.itemName.includes(search));
        return json({ items, totalCount: items.length, page: 1, pageSize: 25 });
      }
      return new Response("missing", { status: 404 });
    }));

    const view = render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    const fast = await screen.findByLabelText("رقم الصنف");
    expect(screen.queryByRole("list")).toBeNull();
    expect(screen.queryByLabelText("قيمة الميزان")).toBeNull();
    expect(screen.queryByRole("button", { name: "إضافة وزن" })).toBeNull();

    await cashier.type(fast, "650{Enter}");
    expect(await screen.findByText(/اسم طويل/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /6501/ })).toBeNull();
    await waitFor(() => expect((fast as HTMLInputElement).value).toBe(""));
    expect(document.activeElement).toBe(fast);

    await cashier.type(fast, "20{Enter}");
    expect((await screen.findAllByText("وزن")).length).toBeGreaterThan(0);
    expect(screen.getByText(/100 غ للوحدة/)).toBeTruthy();
    const portion = screen.getByRole("button", { name: "كمية معسل فرنسي 1 كيلو" });
    expect(portion.textContent).toBe("1");
    const portionQty = portion.closest(".pos-qty");
    expect(portionQty?.getAttribute("dir")).toBe("ltr");
    expect(Array.from(portionQty?.querySelectorAll("button") ?? []).map((node) => node.textContent)).toEqual(["-", "1", "+"]);
    const pricingCalls = calls.filter((call) => call.includes("variable-value")).length;
    await cashier.click(screen.getByRole("button", { name: "زيادة معسل فرنسي 1 كيلو" }));
    expect(screen.getByRole("button", { name: "كمية معسل فرنسي 1 كيلو" }).textContent).toBe("2");
    expect(screen.getByRole("button", { name: "40" })).toBeTruthy();
    const itemName = screen.getByText(/معسل فرنسي 1 كيلو/);
    fast.blur();
    fireEvent.pointerDown(itemName, { clientX: 20, clientY: 20 });
    fireEvent.pointerMove(itemName, { clientX: 20, clientY: 48 });
    fireEvent.pointerUp(itemName, { clientX: 20, clientY: 48 });
    expect(document.activeElement).not.toBe(fast);
    const cartRegion = document.querySelector(".pos-cart-region");
    fast.blur();
    fireEvent.pointerDown(itemName, { clientX: 20, clientY: 20 });
    cartRegion?.dispatchEvent(new Event("scroll", { bubbles: true }));
    fireEvent.pointerUp(itemName, { clientX: 20, clientY: 20 });
    expect(document.activeElement).not.toBe(fast);
    fireEvent.pointerDown(itemName, { clientX: 20, clientY: 20 });
    fireEvent.pointerCancel(itemName);
    expect(document.activeElement).not.toBe(fast);
    fireEvent.pointerDown(itemName, { clientX: 20, clientY: 20, pointerType: "touch" });
    fireEvent.pointerUp(itemName, { clientX: 20, clientY: 20, pointerType: "touch" });
    expect(document.activeElement).not.toBe(fast);
    fireEvent.pointerDown(itemName, { clientX: 20, clientY: 20, pointerType: "mouse" });
    fireEvent.pointerUp(itemName, { clientX: 22, clientY: 21, pointerType: "mouse" });
    await waitFor(() => expect(document.activeElement).toBe(fast));
    fast.blur();
    fireEvent.keyDown(window, { key: "7" });
    expect((fast as HTMLInputElement).value).toBe("7");
    await cashier.clear(fast);
    await cashier.click(screen.getByRole("button", { name: "إنقاص معسل فرنسي 1 كيلو" }));
    expect(screen.getByRole("button", { name: "كمية معسل فرنسي 1 كيلو" }).textContent).toBe("1");
    await cashier.click(screen.getByRole("button", { name: "إنقاص معسل فرنسي 1 كيلو" }));
    expect(screen.getByRole("button", { name: "كمية معسل فرنسي 1 كيلو" }).textContent).toBe("1");
    expect(calls.filter((call) => call.includes("variable-value"))).toHaveLength(pricingCalls);
    expect(screen.getByText((_, node) => node?.textContent === "المجموع")).toBeTruthy();
    expect(document.querySelector(".pos-summary")?.className).toContain("pos-summary");
    expect(calls.some((call) => call.includes("variable-value") && call.includes("20"))).toBe(true);

    await cashier.type(fast, "300{Enter}");
    await waitFor(() => expect(calls.some((call) => call.includes("variable-value") && call.includes("300"))).toBe(true));

    await cashier.type(fast, "0{Enter}");
    expect(await screen.findByText("قيمة الميزان يجب أن تكون أكبر من صفر.")).toBeTruthy();
    expect(calls.filter((call) => call.includes("variable-value"))).toHaveLength(2);

    await cashier.clear(fast);
    await cashier.type(fast, "301{Enter}");
    await waitFor(() => expect(calls.some((call) => call.includes("search=301"))).toBe(true));
    expect(calls.filter((call) => call.includes("variable-value"))).toHaveLength(2);

    await cashier.clear(fast);
    await cashier.type(fast, "#224{Enter}");
    expect(await screen.findByText(/صنف منخفض/)).toBeTruthy();
    expect(calls.filter((call) => call.includes("variable-value"))).toHaveLength(2);

    await cashier.click(screen.getByRole("button", { name: "بحث عن صنف" }));
    await cashier.type(screen.getByLabelText("بحث عن صنف"), "650");
    await cashier.click(screen.getByRole("button", { name: "عرض النتائج" }));
    await waitFor(() => expect(screen.getAllByRole("button", { name: /650/ }).length).toBe(12));
    await cashier.click(screen.getByRole("button", { name: "المزيد" }));
    await waitFor(() => expect(screen.getAllByRole("button", { name: /650/ }).length).toBeGreaterThan(12));
    expect(screen.getByRole("columnheader", { name: /رقم الصنف/ })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: /اسم الصنف/ })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: (name) => name === "السعر" })).toBeTruthy();
    expect(screen.queryByRole("columnheader", { name: "الباركود" })).toBeNull();
    expect(screen.queryByRole("columnheader", { name: "المخزون" })).toBeNull();
    expect(document.querySelector(".pos-search-keys")).toBeNull();
    await cashier.click(screen.getByRole("columnheader", { name: /رقم الصنف/ }));
    expect(screen.getByRole("columnheader", { name: /رقم الصنف/ }).getAttribute("aria-sort")).toBe("ascending");
    await cashier.click(screen.getByRole("columnheader", { name: /رقم الصنف/ }));
    expect(screen.getByRole("columnheader", { name: /رقم الصنف/ }).getAttribute("aria-sort")).toBe("descending");

    const row = document.querySelector(".pos-cart .pos-item-name")?.closest(".pos-line");
    expect(row?.className).toContain("pos-line");
    expect(row?.querySelector(".pos-item-name")).toBeTruthy();
    expect(row?.querySelectorAll("[role='cell']").length).toBe(7);

    const css = readFileSync(resolve("src/styles/app.css"), "utf8");
    expect(css).toContain("grid-template-columns: var(--pos-cart-columns)");
    expect(css).toContain("overflow-wrap: anywhere");
    expect(css).toContain("white-space: normal");
    expect(css).toContain("@media (max-width: 760px)");
    expect(css).toContain("(orientation: landscape) and (max-height: 600px) and (pointer: coarse)");
    expect(css).toContain("(pointer: coarse) and (max-width: 1199px)");
    expect(css).toContain("grid-template-areas: \"main sidebar\"");
    expect(css).toContain("--pos-cart-columns: minmax(16rem, 1fr) 7.5rem 180px 8.5rem 7.5rem 6.5rem");
    expect(css).toContain("flex: 0 0 auto");
    expect(css).toContain("grid-template-rows: repeat(5, minmax(0, 1fr))");
    expect(css).toContain(".pos-overlay-top");
    expect(css).toContain("height: 52px;");
    expect(css).toContain("minmax(172px, 180px) minmax(120px, 136px) minmax(120px, 136px) 76px");
    expect(css).toContain("flex-direction: column");
    expect(css).toContain("position: sticky");
    expect(css).toContain("direction: ltr");
    expect(css).toContain("grid-template-columns: minmax(0, 1fr) clamp(260px, 24%, 330px)");

    expect(calls.some((call) => /\/api\/(stock|sales|payments)/.test(call))).toBe(false);
    view.unmount();
    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    expect(await screen.findByText(/اسم طويل/)).toBeTruthy();
    expect((await screen.findAllByText("وزن")).length).toBeGreaterThan(0);
  });

  it("closes item search only after a successful add and keeps it open after a failure", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const path = String(url);
      if (path.endsWith("/health")) return new Response("ok");
      if (path.includes("/api/pos/cash-session/current")) return json({ session: null });
      if (path.includes("/api/pricing/items/")) {
        const itemId = path.split("/api/pricing/items/")[1]?.split("/")[0] ?? "";
        if (itemId === "id-651") {
          return new Response(JSON.stringify({ message: "الصنف غير متاح للبيع." }), { status: 409, headers: { "Content-Type": "application/json" } });
        }
        return json({ itemId, priceTypeId: "price", priceTypeCode: "Price0", resolvedPrice: 15, priceSource: "CentralDefault" });
      }
      if (path.includes("/api/items")) {
        const search = new URL(path, "http://localhost").searchParams.get("search") ?? "";
        const items = search === "224"
          ? [item("224", "صنف منخفض")]
          : [item("650", "صنف بحث"), item("651", "صنف مرفوض")];
        return json({ items, totalCount: items.length, page: 1, pageSize: 25 });
      }
      return new Response("missing", { status: 404 });
    }));

    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    const fast = await screen.findByLabelText("رقم الصنف");
    await cashier.type(fast, "#224{Enter}");
    expect(await screen.findByText(/صنف منخفض/)).toBeTruthy();

    await cashier.click(screen.getByRole("button", { name: "بحث عن صنف" }));
    const query = screen.getByLabelText("بحث عن صنف");
    await cashier.type(query, "650");
    await cashier.click(screen.getByRole("button", { name: "عرض النتائج" }));
    expect(await screen.findByRole("button", { name: /صنف بحث/ })).toBeTruthy();

    await cashier.click(screen.getByRole("button", { name: /651/ }));
    expect(await screen.findByText("الصنف غير متاح للبيع.")).toBeTruthy();
    expect(screen.getByLabelText("بحث عن صنف")).toBeTruthy();
    expect((screen.getByLabelText("بحث عن صنف") as HTMLInputElement).value).toBe("650");
    expect(screen.getByRole("button", { name: /صنف بحث/ })).toBeTruthy();
    expect(screen.getByText(/صنف منخفض/)).toBeTruthy();

    await cashier.click(screen.getByRole("button", { name: /صنف بحث/ }));
    await waitFor(() => expect(screen.queryByLabelText("بحث عن صنف")).toBeNull());
    expect(screen.queryByRole("button", { name: "650 — صنف بحث" })).toBeNull();
    expect(screen.getByText("صنف بحث")).toBeTruthy();
    expect(screen.getByText(/صنف منخفض/)).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText("رقم الصنف")));

    await cashier.click(screen.getByRole("button", { name: "بحث عن صنف" }));
    await cashier.type(screen.getByLabelText("بحث عن صنف"), "999");
    await cashier.click(screen.getByRole("button", { name: "إغلاق" }));
    expect(screen.queryByLabelText("بحث عن صنف")).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText("رقم الصنف")));
  });

  it("keeps search open on Enter and commits only from the row or إدخال", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      const path = String(url);
      calls.push(path);
      if (path.endsWith("/health")) return new Response("ok");
      if (path.includes("/api/pos/cash-session/current")) return json({ session: null });
      if (path.includes("/api/pricing/items/")) return json({ itemId: "id-443", priceTypeId: "price", priceTypeCode: "Price0", resolvedPrice: 0.5, priceSource: "CentralDefault" });
      if (path.includes("/api/items")) {
        const search = new URL(path, "http://localhost").searchParams.get("search") ?? "";
        const items = search === "443" ? [item("443", "جلدة راس")] : [item("650", "صنف بحث"), item("651", "صنف آخر")];
        return json({ items, totalCount: items.length, page: 1, pageSize: 25 });
      }
      return new Response("missing", { status: 404 });
    }));

    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    await screen.findByLabelText("رقم الصنف");
    await cashier.click(screen.getByRole("button", { name: "بحث عن صنف" }));
    const query = screen.getByLabelText("بحث عن صنف");
    await cashier.type(query, "443{Enter}");
    expect(await screen.findByRole("button", { name: /جلدة راس/ })).toBeTruthy();
    expect(screen.getByLabelText("بحث عن صنف")).toBeTruthy();
    expect(calls.some((call) => call.includes("/api/pricing/"))).toBe(false);
    const dialog = screen.getByRole("dialog", { name: "نافذة البحث" });
    const confirm = within(dialog).getByRole("button", { name: "إدخال" }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);

    await cashier.type(query, "{ArrowDown}");
    expect(confirm.disabled).toBe(false);
    await cashier.click(confirm);
    await waitFor(() => expect(screen.queryByLabelText("بحث عن صنف")).toBeNull());
    expect(screen.getByText("جلدة راس")).toBeTruthy();
  });

  it("opens and closes the branch cash session without checking out or storing it locally", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    let open = false;
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const path = String(url);
      calls.push(`${init?.method ?? "GET"} ${path} ${init?.body ?? ""}`);
      if (path.endsWith("/health")) return new Response("ok");
      if (path.endsWith("/api/pos/cash-session/current")) {
        return json({ session: open ? openSession("25") : null });
      }
      if (path.endsWith("/api/pos/cash-session/open")) {
        expect(init?.body).toBe('{"openingCashAmount":25}');
        open = true;
        return json({ session: openSession("25") });
      }
      if (path.endsWith("/api/pos/cash-session/close")) {
        expect(init?.body).toBe('{"countedCashAmount":20}');
        open = false;
        return json({
          session: {
            ...openSession("25"),
            status: "Closed",
            expectedCashAmount: 25,
            countedCashAmount: 20,
            cashDifference: -5,
            closedAt: "2026-10-22T12:00:00Z",
          },
        });
      }
      return new Response("missing", { status: 404 });
    }));

    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    expect(await screen.findByText(/الوردية مغلقة/)).toBeTruthy();
    expect(screen.getAllByRole("textbox")).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "إتمام البيع" })).toBeNull();
    await cashier.click(screen.getByRole("button", { name: "بدء الوردية" }));
    const start = screen.getByRole("dialog", { name: "بدء الوردية" });
    expect(start.className).toContain("pos-shift");
    expect(within(start).getByText(/المبلغ النقدي الموجود فعليًا/)).toBeTruthy();
    await cashier.type(within(start).getByLabelText("الرصيد الافتتاحي للصندوق"), "25");
    await cashier.click(within(start).getByRole("button", { name: "بدء الوردية" }));
    expect(await screen.findByText(/الوردية مفتوحة/)).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: "بدء الوردية" })).toBeNull();
    expect(screen.getByRole("button", { name: "إتمام البيع" })).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText("رقم الصنف")));

    await cashier.click(screen.getByRole("button", { name: "إغلاق الوردية" }));
    const close = screen.getByRole("dialog", { name: "إغلاق الوردية" });
    expect(within(close).getByText(/المتوقع في الصندوق: 25/)).toBeTruthy();
    await cashier.type(within(close).getByLabelText(/جرد الصندوق/), "20");
    expect(within(close).getByText(/عجز/)).toBeTruthy();
    await cashier.click(within(close).getByRole("button", { name: "إغلاق الوردية" }));
    expect(await screen.findByText(/المبلغ المتوقع 25/)).toBeTruthy();
    expect(screen.getByText(/الفعلي 20/)).toBeTruthy();
    expect(screen.getByText(/الفرق -5/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "بدء الوردية" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "إتمام البيع" })).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText("رقم الصنف")));
    expect(calls.some((call) => /\/api\/(stock|sales|payments)/.test(call))).toBe(false);
    const source = readFileSync(resolve("src/api/cashSession.ts"), "utf8");
    expect(source.includes("indexedDB")).toBe(false);
    const css = readFileSync(resolve("src/styles/app.css"), "utf8");
    expect(css).toContain("width: min(430px, calc(100vw - 32px))");
    expect(css).toContain(".pos-modal-actions");
  });

  it("rejects a bad opening amount and keeps a single fast-entry input", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      if (String(url).endsWith("/health")) return new Response("ok");
      if (String(url).includes("/api/pos/cash-session/current")) return json({ session: null, expectedCashPreview: null });
      if (String(url).includes("/api/pos/cash-session/open")) {
        expect(init?.body).toBe('{"openingCashAmount":0}');
        return json({ session: openSession("0") });
      }
      return new Response("missing", { status: 404 });
    }));
    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    await cashier.click(await screen.findByRole("button", { name: "بدء الوردية" }));
    const start = screen.getByRole("dialog", { name: "بدء الوردية" });
    await cashier.type(within(start).getByLabelText("الرصيد الافتتاحي للصندوق"), "-5");
    await cashier.click(within(start).getByRole("button", { name: "بدء الوردية" }));
    expect(within(start).getByText(/صفراً أو أكبر/)).toBeTruthy();
    expect((within(start).getByLabelText("الرصيد الافتتاحي للصندوق") as HTMLInputElement).value).toBe("-5");
    await cashier.clear(within(start).getByLabelText("الرصيد الافتتاحي للصندوق"));
    await cashier.type(within(start).getByLabelText("الرصيد الافتتاحي للصندوق"), "abc");
    await cashier.click(within(start).getByRole("button", { name: "بدء الوردية" }));
    expect(screen.getByRole("dialog", { name: "بدء الوردية" })).toBeTruthy();
    expect(screen.getAllByRole("textbox")).toHaveLength(2);
    const amount = within(screen.getByRole("dialog", { name: "بدء الوردية" })).getByLabelText("الرصيد الافتتاحي للصندوق");
    await cashier.clear(amount);
    await cashier.type(amount, "0");
    await cashier.click(within(screen.getByRole("dialog", { name: "بدء الوردية" })).getByRole("button", { name: "بدء الوردية" }));
    expect(await screen.findByText(/الوردية مفتوحة/)).toBeTruthy();
  });

  it("loads an existing open shift and blocks closing over an unsold cart", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const path = String(url);
      if (path.endsWith("/health")) return new Response("ok");
      if (path.includes("/api/pos/cash-session/current")) return json({ session: openSession("100"), expectedCashPreview: 120 });
      if (path.includes("/api/pricing/items/")) {
        return json({ itemId: "id-224", priceTypeId: "price", priceTypeCode: "Price0", resolvedPrice: 10, priceSource: "CentralDefault" });
      }
      if (path.includes("/api/items")) return json({ items: [item("224", "صنف منخفض")], totalCount: 1, page: 1, pageSize: 25 });
      return new Response("missing", { status: 404 });
    }));
    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    expect(await screen.findByText(/الوردية مفتوحة/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "بدء الوردية" })).toBeNull();
    expect(screen.getByRole("button", { name: "إتمام البيع" })).toBeTruthy();
    await cashier.type(screen.getByLabelText("رقم الصنف"), "#224{Enter}");
    expect(await screen.findByText(/صنف منخفض/)).toBeTruthy();
    await cashier.click(screen.getByRole("button", { name: "إغلاق الوردية" }));
    expect(screen.getByText(/توجد أصناف في السلة لم يتم إتمام بيعها/)).toBeTruthy();
    expect(screen.queryByLabelText(/جرد الصندوق/)).toBeNull();
    await cashier.click(screen.getByRole("button", { name: "العودة للسلة" }));
    expect(screen.getByText(/صنف منخفض/)).toBeTruthy();
    expect(screen.queryByText(/توجد أصناف في السلة/)).toBeNull();
  });

  it("edits quantity from the current value with the keyboard and keypad", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const path = String(url);
      if (path.endsWith("/health")) return new Response("ok");
      if (path.includes("/api/pos/cash-session/current")) return json({ session: null });
      if (path.includes("/api/pricing/variable-value/resolve")) {
        const body = JSON.parse(String(init?.body ?? "{}")) as { enteredScaleValue?: number };
        return json({
          referenceItemId: "id-1300", referenceItemNumber: "1300", stockOwnerItemId: "owner", stockOwnerItemNumber: "1360",
          enteredScaleValue: body.enteredScaleValue ?? 0, referenceBasePrice: 180, referencePriceSource: "CentralDefault",
          weightKg: 0.1, weightGrams: 100, stockOwnerConversionFactor: 4, projectedStockOwnerQuantity: 0.4,
          variableValueDiscountPercent: 0, rawDiscountedValue: body.enteredScaleValue ?? 0, finalSellingPrice: body.enteredScaleValue ?? 0,
          roundingPolicy: "LegacyVariableValueV1", projectedCost: null, costSource: "Unavailable", branchId: "branch",
        });
      }
      if (path.includes("/api/pricing/items/")) {
        return json({ itemId: "id-224", priceTypeId: "price", priceTypeCode: "Price0", resolvedPrice: 10, priceSource: "CentralDefault" });
      }
      if (path.includes("/api/items")) {
        const search = new URL(path, "http://localhost").searchParams.get("search") ?? "";
        const items = catalog.filter((entry) => entry.itemNumber.includes(search) || entry.itemName.includes(search));
        return json({ items, totalCount: items.length, page: 1, pageSize: 25 });
      }
      return new Response("missing", { status: 404 });
    }));
    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    const fast = await screen.findByLabelText("رقم الصنف");
    await cashier.type(fast, "#224{Enter}");
    const fixedQty = await screen.findByRole("button", { name: "كمية صنف منخفض" });
    await cashier.click(screen.getByRole("button", { name: "زيادة صنف منخفض" }));
    await cashier.click(screen.getByRole("button", { name: "زيادة صنف منخفض" }));
    await cashier.click(screen.getByRole("button", { name: "زيادة صنف منخفض" }));
    expect(fixedQty.textContent).toBe("4");
    await cashier.click(fixedQty);
    const dialog = screen.getByRole("dialog", { name: "تعديل الكمية" });
    const editor = within(dialog).getByLabelText("الكمية") as HTMLInputElement;
    expect(editor.value).toBe("4");
    expect(editor.value).not.toBe("0");
    expect(dialog.querySelector(".pos-qty-title")?.className).toContain("pos-qty-title");
    expect(editor.className).toContain("pos-qty-figure");
    expect(dialog.querySelector(".pos-qty-actions")).toBeTruthy();
    await cashier.keyboard("7");
    expect(editor.value).toBe("7");
    await cashier.keyboard("5");
    expect(editor.value).toBe("75");
    await cashier.keyboard("{Backspace}");
    expect(editor.value).toBe("7");
    await cashier.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "تعديل الكمية" })).toBeNull();
    expect(fixedQty.textContent).toBe("4");
    await waitFor(() => expect(document.activeElement).toBe(fast));

    await cashier.click(fixedQty);
    const again = within(screen.getByRole("dialog", { name: "تعديل الكمية" })).getByLabelText("الكمية") as HTMLInputElement;
    expect(again.value).toBe("4");
    await cashier.click(screen.getByRole("button", { name: "2" }));
    expect(again.value).toBe("2");
    await cashier.click(screen.getByRole("button", { name: "4" }));
    expect(again.value).toBe("24");
    await cashier.keyboard("{Enter}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(fixedQty.textContent).toBe("24");
    await waitFor(() => expect(document.activeElement).toBe(fast));

    await cashier.click(fixedQty);
    const kept = within(screen.getByRole("dialog", { name: "تعديل الكمية" })).getByLabelText("الكمية");
    await cashier.keyboard("{Enter}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(fixedQty.textContent).toBe("24");
    expect(kept).toBeTruthy();

    await cashier.click(fixedQty);
    await cashier.click(screen.getByRole("button", { name: "مسح" }));
    await cashier.keyboard("{Enter}");
    expect(screen.getByText("أدخل كمية صحيحة أكبر من صفر")).toBeTruthy();
    await cashier.click(screen.getByRole("button", { name: "0" }));
    await cashier.keyboard("{NumpadEnter}");
    expect(screen.getByRole("dialog", { name: "تعديل الكمية" })).toBeTruthy();
    await cashier.click(screen.getByRole("button", { name: "1" }));
    await cashier.click(screen.getByRole("button", { name: "0" }));
    expect((screen.getByLabelText("الكمية") as HTMLInputElement).value).toBe("10");
    await cashier.click(screen.getByRole("button", { name: "تأكيد" }));
    expect(fixedQty.textContent).toBe("10");

    await cashier.type(fast, "20{Enter}");
    const weighed = await screen.findByRole("button", { name: "كمية معسل فرنسي 1 كيلو" });
    expect(weighed.textContent).toBe("1");
    await cashier.click(weighed);
    expect((screen.getByLabelText("الكمية") as HTMLInputElement).value).toBe("1");
    await cashier.keyboard("3");
    await cashier.click(screen.getByRole("button", { name: "تأكيد" }));
    expect(weighed.textContent).toBe("3");
    expect(screen.getByText(/100 غ للوحدة/)).toBeTruthy();

    const css = readFileSync(resolve("src/styles/app.css"), "utf8");
    expect(css).toContain("text-align: center");
    expect(css).toContain("font-size: 26px");
    expect(css).toContain("font-size: 34px");
    expect(css).toContain("width: min(400px, calc(100vw - 32px))");
    expect(css).toContain(".pos-qty-actions");
    expect(css).toContain("width: 48px;");
    expect(css).toContain("height: 48px;");
    expect(css).toContain("width: 60px;");
    expect(css).toContain("--pos-cart-columns:");
    expect(css).toContain("grid-template-columns: var(--pos-cart-columns)");
    expect(css).toContain(".pos-col-item");
    expect(css).toContain("text-align: center");
    expect(css).toContain("justify-content: center");
  });

  it("shows the entered number when an item does not exist and leaves the cart unchanged", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    let itemCalls = 0;
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      const path = String(url);
      if (path.endsWith("/health")) return json({ status: "Healthy" });
      if (path.includes("/api/pos/cash-session/current")) return json({ session: null });
      if (path.includes("/api/items") && path.includes("999999")) {
        itemCalls += 1;
        return json({ items: [], totalCount: 0, page: 1, pageSize: 25 });
      }
      if (path.includes("/api/items") && path.includes("888888")) {
        return json({ items: [], totalCount: 0, page: 1, pageSize: 25 });
      }
      if (path.includes("/api/items") && path.includes("server-down")) {
        return new Response("down", { status: 500 });
      }
      if (path.includes("/api/pricing/items/")) {
        return json({ itemId: "id-224", priceTypeId: "price", priceTypeCode: "Price0", resolvedPrice: 10, priceSource: "CentralDefault" });
      }
      if (path.includes("/api/items")) {
        return json({ items: [item("224", "صنف منخفض")], totalCount: 1, page: 1, pageSize: 25 });
      }
      return new Response("missing", { status: 404 });
    }));
    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    const fast = await screen.findByLabelText("رقم الصنف");
    await cashier.type(fast, "#224{Enter}");
    expect(await screen.findByText("صنف منخفض")).toBeTruthy();

    await cashier.type(fast, "999999{Enter}");
    expect(await screen.findByRole("dialog", { name: "تنبيه" })).toBeTruthy();
    expect(screen.getByText("الصنف رقم 999999 غير موجود")).toBeTruthy();
    expect(screen.getByText("صنف منخفض")).toBeTruthy();
    await cashier.keyboard("{Enter}");
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "تنبيه" })).toBeNull());
    expect((fast as HTMLInputElement).value).toBe("");
    await waitFor(() => expect(document.activeElement).toBe(fast));

    await cashier.type(fast, "{Enter}");
    expect(screen.queryByText(/الصنف رقم/)).toBeNull();

    await cashier.type(fast, "888888");
    await cashier.click(screen.getByRole("button", { name: "إضافة" }));
    expect(await screen.findByText("الصنف رقم 888888 غير موجود")).toBeTruthy();
    await cashier.click(screen.getByRole("button", { name: "موافق" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await cashier.type(fast, "server-down{Enter}");
    expect(await screen.findByText("تعذر تحميل الأصناف.")).toBeTruthy();
    expect(screen.queryByText(/غير موجود/)).toBeNull();
    expect(itemCalls).toBe(1);
  });

  it("shows the four customer synchronization states without blocking search", async () => {
    installFakeIndexedDb();
    const cashier = userEvent.setup();
    const states = [
      ["Current", "محدث"],
      ["Offline", "دون اتصال"],
      ["Preparing", "جارٍ التحضير"],
      ["Failed", "تعذرت المزامنة"],
    ] as const;
    for (const [syncState, label] of states) {
      vi.stubGlobal("fetch", vi.fn(async (url: string) => {
        const path = String(url);
        if (path.endsWith("/health")) return json({ status: "Healthy" });
        if (path.includes("/api/pos/cash-session/current")) return json({ session: null });
        if (path.includes("/api/customers")) return json({ customers: [{ customerId: "c1", customerNumber: "1", name: "أحمد", phone: "059", classificationName: null, priceCode: "Price0" }], totalCount: 1, syncState });
        return new Response("missing", { status: 404 });
      }));
      const view = render(<PosScreen session={session} user={user} onBack={() => undefined} />);
      await screen.findByLabelText("رقم الصنف");
      await cashier.click(screen.getByRole("button", { name: /رقم الزبون/ }));
      await cashier.type(screen.getByRole("textbox", { name: "بحث عن زبون" }), "أح");
      await cashier.click(screen.getByRole("button", { name: "عرض النتائج" }));
      expect(await screen.findByText(label)).toBeTruthy();
      expect(screen.getByText("أحمد")).toBeTruthy();
      view.unmount();
    }
  });
});

function openSession(opening: string) {
  return {
    cashSessionId: "session-1",
    status: "Open",
    openedByDisplayName: "سليم",
    openedAt: "2026-10-22T10:00:00Z",
    openingCashAmount: Number(opening),
    expectedCashAmount: null,
    countedCashAmount: null,
    cashDifference: null,
    closedAt: null,
  };
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });
}

function installFakeIndexedDb() {
  const rows = new Map<string, unknown>();
  const database = {
    created: false,
    objectStoreNames: { contains: () => database.created },
    createObjectStore() { this.created = true; },
    transaction() {
      return {
        objectStore: () => ({
          get: (key: string) => complete(rows.get(key)),
          put: (value: { scopeKey: string }) => { rows.set(value.scopeKey, value); return complete(undefined); },
          delete: (key: string) => { rows.delete(key); return complete(undefined); },
        }),
      };
    },
    close() {},
  };
  vi.stubGlobal("indexedDB", {
    open: () => {
      const request = complete(database, false);
      queueMicrotask(() => {
        if (!database.created) request.onupgradeneeded?.();
        request.onsuccess?.();
      });
      return request;
    },
  });
}

function complete(result: unknown, auto = true) {
  const request = {
    result,
    error: null,
    onsuccess: null as null | (() => void),
    onerror: null as null | (() => void),
    onupgradeneeded: null as null | (() => void),
  };
  if (auto) queueMicrotask(() => request.onsuccess?.());
  return request;
}
