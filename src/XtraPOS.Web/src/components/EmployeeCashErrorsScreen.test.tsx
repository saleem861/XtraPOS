import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CashErrorItem, CashErrorList } from "../api/cashErrors";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { EmployeeCashErrorsScreen } from "./EmployeeCashErrorsScreen";
import { HomeScreen } from "./HomeScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const session = {
  deviceId: "d1", businessId: "b", branchId: "br", stationId: "s", deviceName: "صندوق 1", deviceType: "POS", deviceToken: "token",
  registeredAt: "2026-01-01", businessName: "المتجر", branchName: "Labtop", branchType: "Retail", stationName: "كاونتر 1", stationNumber: 1,
} as DeviceSession;

function makeUser(permissions: string[], roles: CurrentUser["roles"] = []): CurrentUser {
  return {
    userId: "u", username: "sup", displayName: "مشرف", roles, permissions,
    businessName: "المتجر", branchName: "Labtop", stationName: "كاونتر 1", deviceName: "صندوق 1",
  } as CurrentUser;
}

function item(itemId: string, amount: number, status: CashErrorItem["status"] = "Open"): CashErrorItem {
  return {
    itemId, kind: "Error", employeeId: "ahmad", employeeName: "أحمد", branchId: "b1", branchName: "Labtop", stationNumber: 1, stationName: "كاونتر 1",
    amount, occurredAt: "2026-11-10T09:00:00", effectiveAt: "2026-11-10T09:00:00", justification: "عد خاطئ", decisionCode: null, status,
    closedBy: null, closedType: null, closedMethod: null, closedAt: null, closedByName: null, deferredTo: null, noteCount: 0,
  };
}

function list(items: CashErrorItem[]): CashErrorList {
  return {
    source: "local",
    notice: "تعذر الوصول إلى الإدارة المركزية. تظهر أخطاء هذا الفرع فقط، والحفظ يعمل ويُرحَّل تلقائياً عند عودة الاتصال.",
    canManageAll: false,
    scopedBranchId: "b1",
    scopedBranchName: "Labtop",
    summary: { count: items.length, openCount: items.length, shortage: -10, surplus: 4, waived: 0, settled: 0, openNet: -6 },
    employees: [{ employeeId: "ahmad", employeeName: "أحمد", summary: { count: 2, openCount: 2, shortage: -10, surplus: 4, waived: 0, settled: 0, openNet: -6 } }],
    items,
    options: {
      branches: [{ id: "b1", name: "Labtop" }, { id: "b1-copy", name: "Labtop" }, { id: "b2", name: "Sharbel" }],
      stations: [{ id: "s1", branchId: "b1", stationNumber: 1, name: "كاونتر 1" }],
      users: [{ id: "ahmad", name: "أحمد" }, { id: "ahmad", name: "أحمد" }],
    },
  };
}

describe("EmployeeCashErrorsScreen", () => {
  it("shows the branch copy with a notice, offsets two errors and reloads", async () => {
    const posted: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      const reply = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: async () => body, headers: new Headers() });
      if (url.startsWith("/api/cash-errors/actions")) {
        posted.push(JSON.parse(String(init?.body)));
        return reply({ actionId: "x", type: "Offset", amount: -6, relayed: false, message: "تم الحفظ على هذا الفرع، ويُرحَّل إلى الإدارة المركزية عند عودة الاتصال." });
      }
      if (url.startsWith("/api/cash-errors")) return reply(list([item("e1", -10), item("e2", 4)]));
      return reply({});
    }));

    render(<EmployeeCashErrorsScreen session={session} user={makeUser(["CashDrawer.Review"])} onBack={() => {}} />);
    expect(await screen.findByText(/تعذر الوصول إلى الإدارة المركزية/)).toBeTruthy();
    expect(screen.getAllByRole("option", { name: "Labtop" })).toHaveLength(1);
    expect(screen.getAllByRole("option", { name: "أحمد" })).toHaveLength(1);
    expect(screen.queryByRole("option", { name: "Sharbel" })).toBeNull();

    await userEvent.click(screen.getAllByRole("checkbox", { name: "اختيار خطأ أحمد" })[0]);
    await userEvent.click(screen.getAllByRole("checkbox", { name: "اختيار خطأ أحمد" })[1]);
    const bar = screen.getByRole("region", { name: "الأخطاء المختارة" });
    expect(within(bar).getByText("-6.00")).toBeTruthy();

    await userEvent.click(within(bar).getByRole("button", { name: /مقاصة/ }));
    const dialog = screen.getByRole("dialog", { name: "مقاصة الأخطاء المختارة" });
    await userEvent.click(within(dialog).getByRole("button", { name: /تأكيد مقاصة/ }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toEqual(expect.objectContaining({ type: "Offset", employeeId: "ahmad", itemIds: ["e1", "e2"] }));
    expect(await screen.findByText(/يُرحَّل إلى الإدارة المركزية/)).toBeTruthy();
  });
});

describe("HomeScreen audit and review menu", () => {
  it("shows sales, errors and drawer review to a supervisor and nothing to a cashier", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve({ ok: true, status: 200, json: async () => ({}) })));
    const { unmount } = render(<HomeScreen session={session} user={makeUser(["CashDrawer.Review"])} pending={false} onLogout={() => {}} onReset={() => {}} />);
    expect(screen.queryByRole("button", { name: /^مراجعة الجرد/ })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: /التدقيق والمراجعة/ }));
    expect(screen.getByRole("button", { name: /^المبيعات/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /^الأخطاء/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /^مراجعة الجرد/ })).toBeTruthy();
    unmount();

    render(<HomeScreen session={session} user={makeUser(["POS.Sell"])} pending={false} onLogout={() => {}} onReset={() => {}} />);
    expect(screen.queryByRole("button", { name: /التدقيق والمراجعة/ })).toBeNull();
  });
});
