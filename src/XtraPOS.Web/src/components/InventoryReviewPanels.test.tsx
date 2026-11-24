import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AuditView, ItemProcessing, SessionDetail } from "../api/inventoryReview";
import { defaultReportRange, filterReportLines, parseOptionalInteger } from "../inventory/inventoryRules";
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

const configuration = { businessDate: "2026-11-24", periodType: "AM", cutoff: "16:00", pageCount: 12, defaultPage: null, importantItems: 0, pageItems: [] };

const report = {
  branchId: "br",
  remote: false,
  from: "2026-11-18",
  to: "2026-11-24",
  employees: [{ userId: "u1", userName: "أحمد" }],
  sessions: [{
    sessionId: "s1", inventoryType: "Important", inventoryTypeLabel: "جرد الأصناف المهمة", status: "Completed", userId: "u1", userName: "أحمد",
    businessDate: "2026-11-24", startedAt: "2026-11-24T06:00:00Z", lastSavedAt: "2026-11-24T06:10:00Z", completedAt: "2026-11-24T06:12:00Z",
    totalItems: 2, countedItems: 2, postedItems: 2, completionPercent: 100, differences: 1, audits: 0, pages: [],
  }],
};

const detail: SessionDetail = {
  session: {
    sessionId: "s1", inventoryType: "Important", inventoryTypeLabel: "جرد الأصناف المهمة", status: "Completed", userName: "أحمد",
    businessDate: "2026-11-24", startedAt: "2026-11-24T06:00:00Z", completedAt: "2026-11-24T06:12:00Z", totalItems: 2, countedItems: 2, postedItems: 2,
  },
  items: [
    { sessionItemId: "l1", itemId: "i1", itemNumber: "650", itemName: "فحم سريع", isCounted: true, countedQuantity: 8, expectedQuantity: 10, difference: -2, countedAt: null, countedByName: "أحمد", posted: true, quantityAfterPosting: 8 },
    { sessionItemId: "l2", itemId: "i2", itemNumber: "224", itemName: "رأس أرجيلة", isCounted: true, countedQuantity: 5, expectedQuantity: 5, difference: 0, countedAt: null, countedByName: "أحمد", posted: true, quantityAfterPosting: 5 },
  ],
  audits: [],
  remote: false,
};

function audit(status: "Open" | "Completed"): AuditView {
  const completed = status === "Completed";
  return {
    audit: {
      auditId: "a1", sessionId: "s1", userName: "المشرف", mine: true, auditMode: "Differences", auditModeLabel: "الفروقات فقط", status,
      startedAt: "2026-11-24T07:00:00Z", completedAt: completed ? "2026-11-24T07:05:00Z" : null, totalItems: 1, countedItems: 1,
      matchedItems: 0, inventoryTypeLabel: "جرد الأصناف المهمة", employeeName: "أحمد", businessDate: "2026-11-24",
    },
    lines: [{
      auditItemId: "x1", itemId: "i1", itemNumber: "650", itemName: "فحم سريع", isCounted: true, auditQuantity: 9, countedAt: null,
      employeeQuantity: completed ? 8 : null, employeeName: completed ? "أحمد" : null, systemQuantity: completed ? 10 : null,
      difference: completed ? 1 : null, matched: completed ? false : null,
    }],
  };
}

const itemView: ItemProcessing = {
  itemId: "i1", itemNumber: "1360", itemName: "معسل فرنسي وقية", scannedItemNumber: "1300", convertedFromPackage: true,
  isImportant: true, inventoryPage: 4, supplyDays: 7, classifiedAt: null, classifiedBy: null,
  onHandQuantity: 12, quantityKnown: true, dailySalesRate: null, corrections: [],
};

describe("inventory report and supervisor audit", () => {
  it("lists the branch counts, keeps the audit blind, then shows the comparison", async () => {
    const calls: string[] = [];
    let completed = false;
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${url}`);
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url === "/api/inventory/report/branches") return reply({ centralReachable: false, branches: [] });
      if (url.startsWith("/api/inventory/report/sessions?")) return reply(report);
      if (url === "/api/inventory/report/sessions/s1") return reply(detail);
      if (url === "/api/inventory/report/sessions/s1/audits") return reply(audit("Open"));
      if (url === "/api/inventory/audits/a1/complete") { completed = true; return reply(audit("Completed")); }
      if (url === "/api/inventory/audits/a1") return reply(audit(completed ? "Completed" : "Open"));
      return reply({});
    }));

    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Inventory.Review"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^تقرير الجرد/ }));
    await userEvent.click(await screen.findByRole("button", { name: /جرد الأصناف المهمة/ }));
    expect(await screen.findByText("فحم سريع")).toBeTruthy();
    expect(screen.queryByText("رأس أرجيلة")).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "الفروقات فقط" }));
    const row = (await screen.findByText("فحم سريع")).closest("article") as HTMLElement;
    expect(within(row).queryByText(/الموظف/)).toBeNull();
    expect(within(row).queryByText(/النظام/)).toBeNull();
    expect(calls).toContain("POST /api/inventory/report/sessions/s1/audits");

    await userEvent.click(screen.getByRole("button", { name: "إنهاء التدقيق" }));
    const dialog = await screen.findByRole("dialog", { name: "إنهاء التدقيق" });
    await userEvent.click(within(dialog).getByRole("button", { name: "إنهاء التدقيق" }));
    const done = (await screen.findByText("الموظف: 8")).closest("article") as HTMLElement;
    expect(within(done).getByText("المشرف: 9")).toBeTruthy();
    expect(within(done).getByText("غير متطابق")).toBeTruthy();
  });

  it("hides the report card from a cashier", () => {
    vi.stubGlobal("fetch", vi.fn(() => reply(configuration)));
    render(<InventoryScreen session={device} permissions={["Inventory.Count"]} onBack={() => undefined} />);
    expect(screen.queryByRole("button", { name: /^تقرير الجرد/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /^معالجة الأصناف/ })).toBeNull();
  });
});

describe("item processing", () => {
  it("shows the stock owner, its classification and the branch quantity", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/inventory/items/lookup")) return reply(itemView);
      return reply({});
    }));
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Inventory.Classify", "Inventory.CorrectQuantity"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^معالجة الأصناف/ }));
    await userEvent.type(screen.getByRole("textbox", { name: "رقم الصنف" }), "1300{Enter}");
    expect(await screen.findByText("معسل فرنسي وقية")).toBeTruthy();
    expect(screen.getByText(/تم تحويل 1300/)).toBeTruthy();
    expect(screen.getByText("الكمية الحالية في الفرع: 12")).toBeTruthy();
    expect((screen.getByRole("checkbox", { name: /صنف مهم/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByRole("button", { name: "تصحيح الكمية" })).toBeTruthy();
  });

  it("refuses a correction without a reason before calling the server", async () => {
    const fetchMock = vi.fn((url: string) => {
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url.startsWith("/api/inventory/items/lookup")) return reply(itemView);
      return reply({});
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Inventory.CorrectQuantity"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^معالجة الأصناف/ }));
    await userEvent.type(screen.getByRole("textbox", { name: "رقم الصنف" }), "1360{Enter}");
    await userEvent.type(await screen.findByRole("textbox", { name: "الكمية الصحيحة" }), "10");
    await userEvent.click(screen.getByRole("button", { name: "تصحيح الكمية" }));
    expect(await screen.findByText("يرجى كتابة سبب التصحيح.")).toBeTruthy();
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes("/quantity"))).toBe(false);
    expect(screen.queryByRole("button", { name: "حفظ التصنيف" })).toBeNull();
  });
});

describe("review rules", () => {
  it("parses optional whole numbers in range", () => {
    expect(parseOptionalInteger("", 1, 365)).toBeNull();
    expect(parseOptionalInteger("١٤", 1, 365)).toBe(14);
    expect(parseOptionalInteger("0", 1, 365)).toBeUndefined();
    expect(parseOptionalInteger("2.5", 1, 365)).toBeUndefined();
  });

  it("filters report lines and defaults the range to seven days", () => {
    const lines = [
      { isCounted: true, difference: -2, expectedQuantity: 10 },
      { isCounted: true, difference: 0, expectedQuantity: 5 },
      { isCounted: true, difference: null, expectedQuantity: null },
      { isCounted: false, difference: null, expectedQuantity: 3 },
    ];
    expect(filterReportLines(lines, "differences")).toHaveLength(2);
    expect(filterReportLines(lines, "counted")).toHaveLength(3);
    expect(filterReportLines(lines, "uncounted")).toHaveLength(1);
    expect(defaultReportRange(new Date(2026, 10, 24))).toEqual({ from: "2026-11-18", to: "2026-11-24" });
  });
});

describe("inventory report across branches", () => {
  it("reads another branch from central, read only", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      calls.push(url);
      if (url.startsWith("/api/inventory/configuration")) return reply(configuration);
      if (url === "/api/inventory/report/branches") {
        return reply({
          centralReachable: true,
          branches: [
            { branchId: "br", branchName: "فرع نابلس", current: true, lastPushAt: null, stale: false },
            { branchId: "br2", branchName: "فرع رام الله", current: false, lastPushAt: "2026-11-24T05:00:00Z", stale: true },
            { branchId: "br3", branchName: "فرع رام الله", current: false, lastPushAt: null, stale: true },
          ],
        });
      }
      if (url.startsWith("/api/inventory/report/sessions?")) {
        return reply(url.includes("branchId=br2") ? { ...report, branchId: "br2", remote: true } : report);
      }
      if (url === "/api/inventory/report/sessions/s1?branchId=br2") return reply({ ...detail, remote: true });
      return reply({});
    }));

    render(<InventoryScreen session={device} permissions={["Inventory.Count", "Inventory.Review"]} onBack={() => undefined} />);
    await userEvent.click(screen.getByRole("button", { name: /^تقرير الجرد/ }));
    const branch = await screen.findByRole("combobox", { name: /^الفرع/ });
    // لا يتكرر اسم في القائمة.
    expect(within(branch).getAllByRole("option").map((option) => option.textContent)).toEqual(["فرع نابلس (هذا الفرع)", "فرع رام الله"]);
    await userEvent.selectOptions(branch, "br2");
    await userEvent.click(screen.getByRole("button", { name: "عرض" }));
    expect(await screen.findByText(/النسخة أقدم من 30 دقيقة/)).toBeTruthy();
    expect(calls.some((url) => url.includes("branchId=br2"))).toBe(true);

    await userEvent.click(await screen.findByRole("button", { name: /جرد الأصناف المهمة/ }));
    expect(await screen.findByText(/للعرض فقط/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "الفروقات فقط" })).toBeNull();
  });
});
