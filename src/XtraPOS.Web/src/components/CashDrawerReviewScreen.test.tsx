import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DeviceSession } from "../types/setup";
import { CashDrawerReviewScreen, reviewChoices } from "./CashDrawerReviewScreen";

describe("cash drawer review choices", () => {
  it("lists each branch name and each point once", () => {
    const choices = reviewChoices({
      branches: [
        { id: "b1", name: "شمس" },
        { id: "b2", name: "شمس" },
        { id: "b3", name: "قمر" },
      ],
      stations: [
        { id: "s1", branchId: "b1", stationNumber: 1, name: "كاونتر" },
        { id: "s2", branchId: "b2", stationNumber: 1, name: "كاونتر" },
        { id: "s3", branchId: "b1", stationNumber: 2, name: "كاونتر" },
      ],
      users: [],
    }, "");

    expect(choices.branches.map((branch) => branch.name)).toEqual(["شمس", "قمر"]);
    expect(choices.stations.map((station) => `${station.stationNumber}-${station.name}`)).toEqual(["1-كاونتر", "2-كاونتر"]);
  });
});

const session = {
  deviceId: "device",
  businessId: "business",
  branchId: "branch",
  stationId: "station",
  deviceName: "صندوق",
  deviceType: "POS",
  deviceToken: "token",
  registeredAt: "2026-11-20T00:00:00Z",
  businessName: "المحل",
  branchName: "الفرع",
  branchType: "Retail",
  stationName: "الصندوق",
  stationNumber: 1,
} as DeviceSession;

describe("cash drawer review display", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the second count beside the summary and keeps the keypad", async () => {
    vi.stubGlobal("matchMedia", () => ({ matches: true, addEventListener() {}, removeEventListener() {} }));
    const line = { displayName: "شيكل", unitValue: 1, quantity: 4, lineAmount: 4 };
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).includes("/reviews/count-2")) {
        return Promise.resolve({
          ok: true,
          json: async () => ({
            count: { countId: "count-2", countedAt: "2026-11-20T10:00:00Z", cashier: "أحمد", branchName: "شمس", stationNumber: 1, stationName: "كاونتر", attempt: 2, countedAmount: 10, difference: -2, status: "Shortage", justification: "نقص الصندوق", final: true, decision: null, decisionNote: null, supervisorName: null, decidedAt: null, lines: [line] },
            firstAttempt: { countId: "count-1", countedAt: "2026-11-20T09:00:00Z", cashier: "أحمد", branchName: "شمس", stationNumber: 1, stationName: "كاونتر", attempt: 1, countedAmount: 8, difference: -4, status: "Shortage", justification: null, final: false, decision: null, decisionNote: null, supervisorName: null, decidedAt: null, lines: [{ ...line, quantity: 2, lineAmount: 2 }] },
          }),
        });
      }
      return Promise.resolve({
        ok: true,
        json: async () => ({
          total: 1,
          shortageTotal: 2,
          lines: [{ countId: "count-2", countedAt: "2026-11-20T10:00:00Z", cashier: "أحمد", branchName: "شمس", stationNumber: 1, stationName: "كاونتر", attempt: 2, countedAmount: 10, difference: -2, status: "Shortage", justification: "نقص الصندوق", final: true, decision: null }],
          options: { branches: [], stations: [], users: [] },
        }),
      });
    }));

    render(<CashDrawerReviewScreen session={session} onBack={() => undefined} />);
    fireEvent.click(await screen.findByRole("button", { name: "عرض" }));
    const first = await screen.findByRole("tab", { name: "فئات العد الأول" });
    const second = screen.getByRole("tab", { name: "فئات العد الثاني" });
    expect(screen.getByLabelText("لوحة المفاتيح")).toBeTruthy();
    expect(screen.getByText("عرض جرد الموظف")).toBeTruthy();
    expect(second.getAttribute("aria-selected")).toBe("true");
    expect(first.getAttribute("aria-selected")).toBe("false");
    expect(screen.getByText("4")).toBeTruthy();
    fireEvent.click(first);
    expect(first.getAttribute("aria-selected")).toBe("true");
    expect(second.getAttribute("aria-selected")).toBe("false");
    expect(screen.getByText("2")).toBeTruthy();
    expect(screen.queryByText("4")).toBeNull();
  });

  it("shows only the first count tab when there is one count", async () => {
    vi.stubGlobal("matchMedia", () => ({ matches: true, addEventListener() {}, removeEventListener() {} }));
    const only = { countId: "count-1", countedAt: "2026-11-20T09:00:00Z", cashier: "أحمد", branchName: "شمس", stationNumber: 1, stationName: "كاونتر", attempt: 1, countedAmount: 8, difference: 0, status: "Matched", justification: null, final: true, decision: null, decisionNote: null, supervisorName: null, decidedAt: null, lines: [{ displayName: "شيكل", unitValue: 1, quantity: 8, lineAmount: 8 }] };
    vi.stubGlobal("fetch", vi.fn((url: string) => Promise.resolve({
      ok: true,
      json: async () => String(url).includes("/reviews/count-1")
        ? { count: only, firstAttempt: null }
        : { total: 1, shortageTotal: 0, lines: [{ ...only }], options: { branches: [], stations: [], users: [] } },
    })));

    render(<CashDrawerReviewScreen session={session} onBack={() => undefined} />);
    fireEvent.click(await screen.findByRole("button", { name: "عرض" }));
    const tab = await screen.findByRole("tab", { name: "فئات العد الأول" });
    expect(tab.getAttribute("aria-selected")).toBe("true");
    expect(screen.queryByRole("tab", { name: "فئات العد الثاني" })).toBeNull();
  });
});
