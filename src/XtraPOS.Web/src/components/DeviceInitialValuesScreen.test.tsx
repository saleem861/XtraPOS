import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  CUSTOMER_DISPLAY_ENABLED,
  type CentralDeviceSettings,
  type CurrentDeviceSettings,
  type DeviceSettingsAuditEntry,
  type DeviceSettingsClient,
  type DeviceSettingsHistory,
} from "../api/deviceSettings";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { CentralDeviceSettingsScreen, DeviceInitialValuesScreen } from "./DeviceInitialValuesScreen";
import { HomeScreen } from "./HomeScreen";

afterEach(() => {
  cleanup();
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

function setting(value: boolean, isDefault = false) {
  return {
    key: CUSTOMER_DISPLAY_ENABLED,
    label: "تفعيل شاشة الزبون",
    kind: "boolean" as const,
    value,
    isDefault,
    updatedAtUtc: isDefault ? null : "2026-11-21T10:00:00.000Z",
    updatedByName: isDefault ? null : "مشرف",
    source: isDefault ? null : ("Branch" as const),
  };
}

function current(value: boolean, syncState: CurrentDeviceSettings["syncState"] = "Synced", isDefault = false): CurrentDeviceSettings {
  return {
    deviceId: "d1",
    branchId: "br",
    branchName: "فرع نابلس",
    posNumber: 1,
    deviceName: "صندوق 1",
    settings: [setting(value, isDefault)],
    syncState,
    pendingTransfers: syncState === "PendingCentral" ? 1 : 0,
    lastError: syncState === "PendingCentral" ? "تعذر الوصول إلى القاعدة المركزية." : null,
    diagnosticId: syncState === "PendingCentral" ? "SETUP-AB12CD34" : null,
    centralVersion: 1,
    syncedAtUtc: null,
  };
}

function fakeClient(overrides: Partial<DeviceSettingsClient>): DeviceSettingsClient {
  return {
    current: vi.fn(async () => current(false, "Synced", true)),
    saveCurrent: vi.fn(async () => current(true)),
    history: vi.fn(async () => ({ transfers: [], received: [] }) as DeviceSettingsHistory),
    centralHistory: vi.fn(async () => [] as DeviceSettingsAuditEntry[]),
    listCentral: vi.fn(async () => [] as CentralDeviceSettings[]),
    saveCentral: vi.fn(),
    ...overrides,
  } as DeviceSettingsClient;
}

describe("DeviceInitialValuesScreen", () => {
  it("saves the customer display toggle for the current point only", async () => {
    const history: DeviceSettingsHistory = {
      transfers: [
        {
          outboxEventId: "e1",
          changedKeys: [CUSTOMER_DISPLAY_ENABLED],
          createdByName: "مشرف",
          status: "Pending",
          attemptCount: 2,
          createdAt: "2026-11-21T10:00:00.000Z",
          lastAttemptAt: "2026-11-21T10:01:00.000Z",
          nextRetryAt: "2026-11-21T10:02:00.000Z",
          completedAt: null,
          centralVersion: null,
          lastError: "تعذر الوصول إلى القاعدة المركزية.",
          diagnosticId: "SETUP-AB12CD34",
        },
      ],
      received: [],
    };
    const client = fakeClient({
      saveCurrent: vi.fn(async () => current(true, "PendingCentral")),
      history: vi.fn(async () => history),
    });
    render(<DeviceInitialValuesScreen session={session} user={makeUser(["Devices.ConfigureCurrent"])} onBack={() => {}} client={client} />);

    const toggle = await screen.findByRole("checkbox", { name: "تفعيل شاشة الزبون" });
    expect((toggle as HTMLInputElement).checked).toBe(false);
    const save = screen.getByRole("button", { name: "حفظ" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(toggle);
    expect((save as HTMLButtonElement).disabled).toBe(false);
    await userEvent.click(save);

    expect(client.saveCurrent).toHaveBeenCalledWith({ [CUSTOMER_DISPLAY_ENABLED]: true });
    expect(await screen.findByText("تم حفظ القيم الأولية لهذه النقطة.")).toBeTruthy();
    const status = screen.getByRole("status").textContent ?? "";
    expect(status).toContain("بانتظار الترحيل إلى الإدارة المركزية");
    expect(status).toContain("SETUP-AB12CD34");

    const log = await screen.findByRole("region", { name: "سجل الترحيلات" });
    expect(log.textContent).toContain("بانتظار الترحيل");
    expect(log.textContent).toContain("رمز التشخيص: SETUP-AB12CD34");
    expect(log.textContent).toContain("شاشة الزبون");
  });

  it("shows the server message when saving is refused", async () => {
    const client = fakeClient({
      saveCurrent: vi.fn(async () => {
        throw new Error("ضبط القيم الأولية متاح للمدير والمشرف فقط.");
      }),
    });
    render(<DeviceInitialValuesScreen session={session} user={makeUser([])} onBack={() => {}} client={client} />);

    await userEvent.click(await screen.findByRole("checkbox", { name: "تفعيل شاشة الزبون" }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText("ضبط القيم الأولية متاح للمدير والمشرف فقط.")).toBeTruthy();
  });
});

describe("CentralDeviceSettingsScreen", () => {
  it("edits the selected point of another branch", async () => {
    const devices: CentralDeviceSettings[] = [
      { deviceId: "a", branchId: "b1", branchName: "فرع نابلس", posNumber: 1, deviceName: "صندوق 1", settings: [setting(false, true)], settingsVersion: 0, updatedAtUtc: null, updatedSource: null },
      { deviceId: "b", branchId: "b2", branchName: "فرع رام الله", posNumber: 2, deviceName: "صندوق 2", settings: [setting(true)], settingsVersion: 3, updatedAtUtc: null, updatedSource: "Branch" },
    ];
    const saveCentral = vi.fn(async (deviceId: string) => ({ ...devices[1], deviceId, settings: [setting(false)], settingsVersion: 4 }));
    const client = fakeClient({ listCentral: vi.fn(async () => devices), saveCentral });
    render(<CentralDeviceSettingsScreen session={session} onBack={() => {}} client={client} />);

    await userEvent.selectOptions(await screen.findByLabelText("نقطة البيع"), "b");
    const toggle = screen.getByRole("checkbox", { name: "تفعيل شاشة الزبون" });
    expect((toggle as HTMLInputElement).checked).toBe(true);
    await userEvent.click(toggle);
    await userEvent.click(screen.getByRole("button", { name: "حفظ للنقطة المختارة" }));

    expect(saveCentral).toHaveBeenCalledWith("b", { [CUSTOMER_DISPLAY_ENABLED]: false });
    expect(await screen.findByText(/تم الحفظ في الإدارة المركزية/)).toBeTruthy();
    expect(client.centralHistory).toHaveBeenCalledWith("b");
  });
});

describe("HomeScreen settings menu", () => {
  it("shows the initial values button to a supervisor and hides it from a cashier", async () => {
    const { unmount } = render(
      <HomeScreen session={session} user={makeUser(["Devices.ConfigureCurrent"])} pending={false} onLogout={() => {}} onReset={() => {}} />,
    );
    await userEvent.click(screen.getByRole("button", { name: /الإعدادات/ }));
    expect(screen.getByRole("button", { name: "القيم الأولية للنقطة الحالية" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "إعدادات نقاط الفروع" })).toBeNull();
    unmount();

    render(<HomeScreen session={session} user={makeUser(["POS.Sell"])} pending={false} onLogout={() => {}} onReset={() => {}} />);
    expect(screen.queryByRole("button", { name: /الإعدادات/ })).toBeNull();
  });
});
