import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { UnregisteredDeviceScreen } from "./UnregisteredDeviceScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("local main device recovery", () => {
  it("lists the current branch database and opens its binding form", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      const href = String(url);
      if (href.includes("/api/setup/branch-databases")) {
        return new Response(JSON.stringify({
          databases: [{
            branchName: "فرع نابلس",
            branchCode: "01",
            databaseName: "XtraPOS_Branch_Test_Nablus",
            businessName: "مؤسسة النور",
            server: "localhost",
          }],
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      if (href.includes("/api/setup/local-device-recovery")) {
        return new Response(JSON.stringify({
          businessName: "مؤسسة النور",
          branchId: "11111111-1111-1111-1111-111111111111",
          branchName: "فرع نابلس",
          branchType: "Retail",
          suggestedDeviceName: "صندوق 1",
          stations: [{ stationId: "e20bec92-d3bd-f111-ae0a-00e18cc5c023", stationNumber: 1, stationName: "كاونتر 1" }],
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      throw new Error(href);
    }));

    render(<UnregisteredDeviceScreen onRecovered={() => undefined} />);

    await user.click(await screen.findByRole("button", { name: /فرع نابلس/ }));
    expect(await screen.findByText("مؤسسة النور")).toBeTruthy();
    expect(screen.getByText("فرع نابلس")).toBeTruthy();
    expect(screen.getByRole("option", { name: "كاونتر 1" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "ربط هذا الجهاز" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /الانضمام إلى فرع موجود/ })).toBeNull();
  });
});
