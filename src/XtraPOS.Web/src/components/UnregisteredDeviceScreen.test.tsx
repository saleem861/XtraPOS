import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { UnregisteredDeviceScreen } from "./UnregisteredDeviceScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("local main device recovery", () => {
  it("offers binding to the current branch and keeps remote join unavailable", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({
        businessName: "مؤسسة النور",
        branchName: "فرع نابلس",
        branchType: "Retail",
        suggestedDeviceName: "صندوق 1",
        stations: [{ stationId: "e20bec92-d3bd-f111-ae0a-00e18cc5c023", stationNumber: 1, stationName: "كاونتر 1" }],
      }), { status: 200, headers: { "Content-Type": "application/json" } })),
    );

    render(<UnregisteredDeviceScreen onRecovered={() => undefined} />);

    expect(await screen.findByRole("button", { name: "ربط هذا الجهاز بالفرع الحالي" })).toBeTruthy();
    expect(screen.getByText("مؤسسة النور")).toBeTruthy();
    expect(screen.getByText("فرع نابلس")).toBeTruthy();
    expect(screen.getByRole("option", { name: "كاونتر 1" })).toBeTruthy();
    const remoteJoin = screen.getByRole("button", { name: /الانضمام إلى فرع موجود/ });
    expect(remoteJoin.hasAttribute("disabled")).toBe(true);
    expect(remoteJoin.textContent).toContain("قريباً");
  });
});
