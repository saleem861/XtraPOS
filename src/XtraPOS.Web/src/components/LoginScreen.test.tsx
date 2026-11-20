import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DeviceSession } from "../types/setup";
import { LoginScreen, updateStageText } from "./LoginScreen";

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

describe("login update button", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("names each updater stage", () => {
    expect(updateStageText("Downloading", "20261121-000042")).toBe("جاري تنزيل النسخة 20261121-000042");
    expect(updateStageText("RollingBack", "")).toBe("فشل التحديث، جاري إرجاع النسخة السابقة");
  });

  it("shows the updater failure reason instead of waiting", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url === "/api/local-update") {
        return Promise.resolve({ ok: true, json: async () => ({ mode: "updater", requestId: "r1" }) });
      }
      return Promise.resolve({
        ok: true,
        json: async () => ({
          installed: "20261120-224712",
          updaterAlive: true,
          status: { requestId: "r1", state: "Failed", stage: "Done", detail: "rolled back: no files copied", from: "20261120-224712", to: "20261121-000042", at: "" },
        }),
      });
    }));

    render(<LoginScreen session={session} pending={false} error="" onSubmit={() => undefined} />);
    fireEvent.click(screen.getByRole("button", { name: "تحديث التطبيق" }));
    expect(await screen.findByText(/فشل التحديث وبقيت النسخة السابقة تعمل/, undefined, { timeout: 5000 })).toBeTruthy();
    expect(screen.getByText(/rolled back: no files copied/)).toBeTruthy();
  }, 10000);

  it("says when the installed version is already the latest", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url === "/api/local-update") {
        return Promise.resolve({ ok: true, json: async () => ({ mode: "updater", requestId: "r2" }) });
      }
      return Promise.resolve({
        ok: true,
        json: async () => ({
          installed: "20261121-000042",
          updaterAlive: true,
          status: { requestId: "r2", state: "UpToDate", stage: "Done", detail: "", from: "20261121-000042", to: "20261121-000042", at: "" },
        }),
      });
    }));

    render(<LoginScreen session={session} pending={false} error="" onSubmit={() => undefined} />);
    fireEvent.click(screen.getByRole("button", { name: "تحديث التطبيق" }));
    const result = await screen.findByText("التطبيق محدّث إلى آخر نسخة (20261121-000042).", undefined, { timeout: 5000 });
    expect(result.className).toContain("is-ok");
    expect(result.compareDocumentPosition(screen.getByRole("button", { name: "تسجيل الدخول" })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  }, 10000);

  it("fills the ring by updater stage instead of counting down from 300", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (url === "/api/local-update") {
        return Promise.resolve({ ok: true, json: async () => ({ mode: "updater", requestId: "r3" }) });
      }
      return Promise.resolve({
        ok: true,
        json: async () => ({
          installed: "20261121-001534",
          updaterAlive: true,
          status: { requestId: "r3", state: "Running", stage: "Backup", detail: "", from: "20261121-001534", to: "20261121-003412", at: "" },
        }),
      });
    }));

    render(<LoginScreen session={session} pending={false} error="" onSubmit={() => undefined} />);
    fireEvent.click(screen.getByRole("button", { name: "تحديث التطبيق" }));
    expect(await screen.findByText("3 من 6", undefined, { timeout: 5000 })).toBeTruthy();
    expect(screen.getByText("جاري حفظ نسخة احتياطية")).toBeTruthy();
    expect(screen.queryByText("300")).toBeNull();
  }, 10000);
});
