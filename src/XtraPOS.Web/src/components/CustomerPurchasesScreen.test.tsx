import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { appendPage, starGlyphs, type PurchaseLine, type PurchasesPage } from "../api/customerPurchases";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { CustomerPurchasesScreen } from "./CustomerPurchasesScreen";
import { thresholdsProblem } from "./CustomerStarsSettingsScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const session = {
  deviceId: "d1", businessId: "b", branchId: "br", stationId: "s", deviceName: "صندوق 1", deviceType: "POS", deviceToken: "token",
  registeredAt: "2026-01-01", businessName: "المتجر", branchName: "Labtop", branchType: "Retail", stationName: "كاونتر 1", stationNumber: 1,
} as DeviceSession;

const user = {
  userId: "u", username: "cashier", displayName: "أمين", roles: [], permissions: ["POS.Sell"],
  businessName: "المتجر", branchName: "Labtop", stationName: "كاونتر 1", deviceName: "صندوق 1",
} as CurrentUser;

function line(n: number): PurchaseLine {
  return {
    saleId: `s${n}`, saleNumber: 1000 + n, completedAt: "2026-11-20T10:00:00Z", branchName: "Labtop", stationNumber: 1, stationName: "كاونتر 1",
    cashier: "أمين", total: 10, lineCount: 2, earn: null, itemQuantity: null, itemTotal: null,
  };
}

function page(skip: number, count: number, hasMore: boolean): PurchasesPage {
  return {
    source: "central",
    notice: null,
    customer: { customerId: "c1", customerNumber: "55", name: "سعاد", phone: "0599" },
    seeTotals: false,
    totals: { count: 75, salesTotal: null, earnTotal: null, itemNumber: null, itemName: null, itemQuantity: null, itemTotal: null },
    stars: skip === 0
      ? { stars: 3, windowDays: 90, windowTotal: 0, previousTotal: 0, trend: "Up", changePercent: 25, nextStarAt: null, neededForNext: null, thresholdsAreDefault: true }
      : null,
    skip,
    hasMore,
    lines: Array.from({ length: count }, (_, index) => line(skip + index)),
  };
}

describe("CustomerPurchasesScreen", () => {
  it("opens on the sale's customer, shows the stars and loads the next page on demand", async () => {
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      calls.push(url);
      const skip = Number(new URL(url, "http://local").searchParams.get("skip"));
      const body = skip === 0 ? page(0, 50, true) : page(50, 25, false);
      return Promise.resolve({ ok: true, status: 200, json: async () => body });
    }));

    render(<CustomerPurchasesScreen session={session} user={user} initialCustomer={{ customerId: "c1", customerNumber: "55", name: "سعاد" }} onBack={() => {}} />);
    expect(await screen.findByText("في تقدم +25%")).toBeTruthy();
    expect(screen.getByLabelText("3 من 5 نجوم")).toBeTruthy();
    expect(calls[0]).toContain("/api/customers/c1/purchases?skip=0&take=50");
    expect(screen.queryByText("مجموع المشتريات")).toBeNull();

    await userEvent.click(await screen.findByRole("button", { name: /عرض المزيد/ }));
    await waitFor(() => expect(screen.getByText("75 فاتورة")).toBeTruthy());
    expect(calls.some((url) => url.includes("skip=50"))).toBe(true);
  });

  it("does not open purchases for the general customer", () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve({ ok: true, status: 200, json: async () => ({ items: [], total: 0 }) })));
    render(<CustomerPurchasesScreen session={session} user={user} initialCustomer={{ customerId: "g", customerNumber: "1" }} onBack={() => {}} />);
    expect(screen.queryByText("سعاد")).toBeNull();
  });
});

describe("customer star helpers", () => {
  it("draws five stars, merges pages without repeats and checks the thresholds", () => {
    expect(starGlyphs(3)).toBe("★★★☆☆");
    expect(appendPage([line(1), line(2)], [line(2), line(3)]).map((row) => row.saleId)).toEqual(["s1", "s2", "s3"]);
    expect(thresholdsProblem(90, { oneStar: 100, twoStars: 500, threeStars: 1500, fourStars: 3000, fiveStars: 6000 })).toBe("");
    expect(thresholdsProblem(3, { oneStar: 100, twoStars: 500, threeStars: 1500, fourStars: 3000, fiveStars: 6000 })).toContain("7");
    expect(thresholdsProblem(90, { oneStar: 100, twoStars: 50, threeStars: 1500, fourStars: 3000, fiveStars: 6000 })).toContain("نجمتان");
  });
});
