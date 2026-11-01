import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { HomeScreen } from "./HomeScreen";
import type { CurrentUser, DeviceSession } from "../types/setup";

afterEach(() => {
  cleanup();
});

const session = {
  deviceId: "d",
  businessId: "b",
  branchId: "br",
  stationId: "s",
  deviceName: "Till",
  deviceType: "POS",
  deviceToken: "token",
  registeredAt: "2026-01-01",
  businessName: "المتجر",
  branchName: "الفرع",
  branchType: "Retail",
  stationName: "كاونتر",
  stationNumber: 1,
} as DeviceSession;

const user = {
  userId: "u",
  username: "cashier",
  displayName: "أمين",
  roles: [],
  permissions: ["Items.View", "Customers.View", "Customers.Manage"],
  businessName: "المتجر",
  branchName: "الفرع",
  stationName: "كاونتر",
  deviceName: "Till",
} as CurrentUser;

describe("customers menu", () => {
  it("opens the management screen and leaves the four cards inactive", async () => {
    const onLogout = vi.fn();
    render(
      <div className="app">
        <header className="topbar">
          <h1 className="topbar-title"><span id="app-title-slot" /></h1>
          <div id="app-back-slot" />
        </header>
        <HomeScreen session={session} user={user} pending={false} onLogout={onLogout} onReset={() => undefined} />
      </div>,
    );

    expect(screen.getAllByRole("button", { name: /الزبائن/ })).toHaveLength(1);
    await userEvent.click(screen.getByRole("button", { name: /الزبائن/ }));

    expect(screen.getByRole("heading", { name: "إدارة الزبائن" })).toBeTruthy();
    expect(document.querySelectorAll(".xp-screen-bar")).toHaveLength(0);
    expect(document.querySelectorAll("header")).toHaveLength(1);
    for (const title of ["ديون الزبائن", "مشتريات الزبون"]) {
      expect(screen.getByRole("button", { name: new RegExp(title) }).hasAttribute("disabled")).toBe(true);
    }
    expect(screen.getByRole("button", { name: /إضافة زبون جديد/ }).hasAttribute("disabled")).toBe(false);
    expect(screen.getByRole("button", { name: /استعراض الزبائن/ }).hasAttribute("disabled")).toBe(false);

    await userEvent.click(screen.getByRole("button", { name: "رجوع" }));
    expect(screen.getByRole("heading", { name: "الرئيسية" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "رجوع" })).toBeNull();
    expect(document.querySelectorAll("header")).toHaveLength(1);
    expect(document.querySelectorAll(".xp-screen-bar")).toHaveLength(0);
    expect(screen.getByRole("button", { name: "نقطة البيع" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "الأصناف" })).toBeTruthy();
    expect(screen.getByText("المتجر")).toBeTruthy();
    expect(screen.getByText("كاونتر")).toBeTruthy();
  });

  it("lets a user create a customer and keeps editing closed", async () => {
    const clerk = { ...user, permissions: ["Customers.View", "Customers.Create"] };
    render(
      <div className="app">
        <header className="topbar"><h1 className="topbar-title"><span id="app-title-slot" /></h1><div id="app-back-slot" /></header>
        <HomeScreen session={session} user={clerk} pending={false} onLogout={() => undefined} onReset={() => undefined} />
      </div>,
    );
    await userEvent.click(screen.getByRole("button", { name: /الزبائن/ }));
    expect(screen.getByRole("button", { name: /إضافة زبون جديد/ }).hasAttribute("disabled")).toBe(false);
    expect(screen.getByRole("button", { name: /استعراض الزبائن/ }).hasAttribute("disabled")).toBe(false);
  });

  it("uses a pointer for enabled customer cards and not-allowed for inactive ones", () => {
    const css = readFileSync(resolve("src/styles/app.css"), "utf8");
    const card = css.slice(css.indexOf(".customers-menu-card {"), css.indexOf(".customers-menu-icon {"));
    expect(card).toContain("cursor: pointer;");
    expect(card).toContain(".customers-menu-card:disabled");
    expect(card.slice(card.indexOf(".customers-menu-card:disabled"))).toContain("cursor: not-allowed;");
    expect(css).toContain(".customer-search-content .action-row button:not(:disabled)");
  });
});
