import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CUSTOMER_DISPLAY_ENABLED, type PointSettings, type PointsList } from "../api/deviceSettings";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { DeviceInitialValuesScreen } from "./DeviceInitialValuesScreen";
import { HomeScreen } from "./HomeScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const session = {
  deviceId: "d1",
  businessId: "b",
  branchId: "br",
  stationId: "s",
  deviceName: "صندوق 1",
  deviceType: "POS",
  deviceToken: "token",
  registeredAt: "2026-01-01",
  businessName: "المتجر",
  branchName: "فرع نابلس",
  branchType: "Retail",
  stationName: "كاونتر 1",
  stationNumber: 1,
} as DeviceSession;

function makeUser(permissions: string[]): CurrentUser {
  return {
    userId: "u",
    username: "sup",
    displayName: "مشرف",
    roles: [],
    permissions,
    businessName: "المتجر",
    branchName: "فرع نابلس",
    stationName: "كاونتر 1",
    deviceName: "صندوق 1",
  } as CurrentUser;
}

function point(deviceId: string, branchName: string, posNumber: number, enabled: boolean, isCurrent = false): PointSettings {
  return {
    deviceId,
    branchId: `branch-${branchName}`,
    branchName,
    posNumber,
    deviceName: `صندوق ${posNumber}`,
    isCurrent,
    settings: [{ key: CUSTOMER_DISPLAY_ENABLED, label: "تفعيل شاشة الزبون", kind: "boolean", value: enabled, isDefault: !enabled, updatedAtUtc: null, updatedByName: null, source: null }],
    centralVersion: 0,
    syncedAtUtc: null,
    updatedSource: null,
  };
}

function stubServer(list: PointsList) {
  const saved: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
    const reply = (body: unknown, status = 200) => Promise.resolve({ ok: status < 400, status, json: async () => body });
    if (url === "/api/device-settings/points") return reply(list);
    if (url === "/api/device-settings" && init?.method === "PUT") {
      const body = JSON.parse(String(init.body));
      saved.push(body);
      return reply({ operationId: "op", points: body.scope === "All" ? list.points.length : 1, message: "تم حفظ القيم الأولية لهذه النقطة، وأُضيفت إلى سجل الترحيل." });
    }
    return reply({});
  }));
  return saved;
}

describe("DeviceInitialValuesScreen", () => {
  it("opens on the current point and saves it through the relay log", async () => {
    const saved = stubServer({ points: [point("d1", "فرع نابلس", 1, false, true)], canManageAll: false, notice: null });
    render(<DeviceInitialValuesScreen session={session} user={makeUser(["Devices.ConfigureCurrent"])} onBack={() => {}} />);

    const picker = await screen.findByRole("combobox");
    await waitFor(() => expect((picker as HTMLSelectElement).value).toBe("d1"));
    expect((picker as HTMLSelectElement).disabled).toBe(true);
    expect(screen.queryByRole("option", { name: "جميع النقاط في كل الفروع" })).toBeNull();

    const save = screen.getByRole("button", { name: "حفظ" }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    await userEvent.click(screen.getByRole("checkbox", { name: "تفعيل شاشة الزبون" }));
    await userEvent.click(save);

    expect(await screen.findByText(/أُضيفت إلى سجل الترحيل/)).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
    expect(saved).toEqual([expect.objectContaining({ scope: "Device", deviceId: "d1", values: { [CUSTOMER_DISPLAY_ENABLED]: true } })]);
  });

  it("lets the manager pick a point in another branch or all points", async () => {
    const saved = stubServer({
      points: [point("d1", "فرع نابلس", 1, false, true), point("d9", "فرع رام الله", 4, true)],
      canManageAll: true,
      notice: null,
    });
    render(<DeviceInitialValuesScreen session={session} user={makeUser(["Devices.ManageAll"])} onBack={() => {}} />);

    const picker = await screen.findByRole("combobox") as HTMLSelectElement;
    await waitFor(() => expect(picker.value).toBe("d1"));
    expect(picker.disabled).toBe(false);

    fireEvent.change(picker, { target: { value: "d9" } });
    const checkbox = screen.getByRole("checkbox", { name: "تفعيل شاشة الزبون" }) as HTMLInputElement;
    expect(checkbox.checked).toBe(true);
    await userEvent.click(checkbox);
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toEqual(expect.objectContaining({ scope: "Device", deviceId: "d9", point: expect.objectContaining({ branchName: "فرع رام الله" }) }));

    fireEvent.change(picker, { target: { value: "all" } });
    await userEvent.click(screen.getByRole("checkbox", { name: "تفعيل شاشة الزبون" }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ لجميع النقاط" }));
    await waitFor(() => expect(saved).toHaveLength(2));
    expect(saved[1]).toEqual({ scope: "All", values: { [CUSTOMER_DISPLAY_ENABLED]: true } });
  });
});

describe("HomeScreen settings menu", () => {
  it("shows one initial values entry to a supervisor and hides settings from a cashier", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve({ ok: true, status: 200, json: async () => ({}) })));
    const { unmount } = render(
      <HomeScreen session={session} user={makeUser(["Devices.ConfigureCurrent"])} pending={false} onLogout={() => {}} onReset={() => {}} />,
    );
    await userEvent.click(screen.getByRole("button", { name: /الإعدادات/ }));
    expect(screen.getByRole("button", { name: "القيم الأولية" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "إعدادات نقاط الفروع" })).toBeNull();
    unmount();

    render(<HomeScreen session={session} user={makeUser(["POS.Sell"])} pending={false} onLogout={() => {}} onReset={() => {}} />);
    expect(screen.queryByRole("button", { name: /الإعدادات/ })).toBeNull();
  });
});
