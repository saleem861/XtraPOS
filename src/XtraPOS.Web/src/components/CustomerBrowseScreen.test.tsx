import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DeviceSession } from "../types/setup";
import { CustomerBrowseScreen } from "./CustomerBrowseScreen";

const session = {
  deviceId: "device",
  businessId: "business",
  branchId: "branch",
  stationId: "station",
  deviceName: "صندوق",
  deviceType: "POS",
  deviceToken: "token",
  registeredAt: "2026-10-22",
  businessName: "المتجر",
  branchName: "فرع",
  branchType: "Retail",
  stationName: "كاونتر",
  stationNumber: 1,
} as DeviceSession;

function json(body: unknown) {
  return Promise.resolve({ ok: true, status: 200, json: async () => body } as Response);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function stubMedia() {
  vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: false, addEventListener: () => undefined, removeEventListener: () => undefined })));
}

describe("استعراض الزبائن", () => {
  it("shows classification rows and opens the selected customer read-only for a user", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      const path = String(url);
      if (path.includes("/api/customers?")) {
        return json({
          totalCount: 2,
          customers: [
            { customerId: "customer-1", customerNumber: "0592594707", name: "سليم", phone: "0592594707", classificationName: "عادي", priceCode: "Price0", variableValueDiscountPercent: 0 },
            { customerId: "customer-2", customerNumber: "100", name: "محل النور", phone: "0590000002", classificationName: "محل تجاري", priceCode: "Dealer", variableValueDiscountPercent: 31 },
          ],
        });
      }
      if (path.endsWith("/classifications")) return json({ classifications: [] });
      if (path.endsWith("/price-types")) return json([]);
      if (path.endsWith("/calling-codes")) return json([]);
      return json({ customerId: "customer-1", name: "سليم", customerNumber: "0592594707", classificationName: "عادي", rowVersion: "AQIDBAUGBwg=" });
    }));

    stubMedia();
    render(<CustomerBrowseScreen session={session} permissions={["Customers.View", "Customers.Create"]} onBack={() => undefined} />);
    await userEvent.type(screen.getByLabelText("بحث عن زبون"), "سليم");
    expect(await screen.findByText("0592594707")).toBeTruthy();
    expect(screen.getByText("عادي")).toBeTruthy();
    expect(screen.getByText("محل تجاري")).toBeTruthy();
    expect(screen.queryByText("Price0")).toBeNull();
    expect(screen.queryByText("Dealer")).toBeNull();
    const rows = document.querySelectorAll(".customer-browse-row:not(.customer-browse-head)");
    expect(rows[0]?.className).toBe("customer-browse-row");
    expect(rows[1]?.className).toContain("is-alt");
    await userEvent.click(screen.getAllByRole("button", { name: "عرض" })[0]);
    expect(await screen.findByLabelText("اسم الزبون")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "حفظ الزبون" })).toBeNull();
    expect(screen.queryByRole("button", { name: "حذف" })).toBeNull();
  });

  it("keeps save available when the role can edit", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      const path = String(url);
      if (path.includes("/api/customers?")) {
        return json({
          totalCount: 1,
          customers: [{ customerId: "customer-1", customerNumber: "1", name: "سليم", classificationName: "عادي", priceCode: "Price0" }],
        });
      }
      if (path.endsWith("/classifications")) return json({ classifications: [] });
      if (path.endsWith("/price-types")) return json([]);
      if (path.endsWith("/calling-codes")) return json([]);
      return json({ customerId: "customer-1", name: "سليم", customerNumber: "1", classificationName: "عادي", rowVersion: "AQIDBAUGBwg=" });
    }));
    stubMedia();
    render(<CustomerBrowseScreen session={session} permissions={["Customers.Manage"]} onBack={() => undefined} />);
    await userEvent.type(screen.getByLabelText("بحث عن زبون"), "س");
    await userEvent.click(await screen.findByRole("button", { name: "عرض" }));
    expect(await screen.findByRole("button", { name: "حفظ" })).toBeTruthy();
  });

  it("asks for confirmation before deleting a customer", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === "DELETE") return json({ status: "Deleted" });
      return json({
        totalCount: 1,
        customers: [{ customerId: "customer-1", customerNumber: "100", name: "محل النور", classificationName: "محل تجاري" }],
      });
    }));
    stubMedia();
    render(<CustomerBrowseScreen session={session} permissions={["Customers.Manage"]} onBack={() => undefined} />);
    await userEvent.type(screen.getByLabelText("بحث عن زبون"), "محل");
    await userEvent.click(await screen.findByRole("button", { name: "حذف" }));
    expect(screen.getAllByText("محل النور").length).toBeGreaterThan(1);
    expect(screen.getAllByText("100").length).toBeGreaterThan(1);
    expect(screen.getByText("هل أنت متأكد من حذف هذا الزبون؟ لا يمكن التراجع عن عملية الحذف.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "إلغاء" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "تأكيد الحذف" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "إلغاء" }));
    expect(screen.queryByRole("button", { name: "تأكيد الحذف" })).toBeNull();
  });
});
