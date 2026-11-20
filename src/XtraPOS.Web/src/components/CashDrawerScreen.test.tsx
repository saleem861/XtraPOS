import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DeviceSession } from "../types/setup";
import { CashDrawerScreen } from "./CashDrawerScreen";

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

const denomination = { denominationId: "coin", unitValue: 1, displayName: "شيكل", sortOrder: 1 };
const sentence = "المجموع المعدود لا يطابق الرصيد المتوقع. أعد العد.";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("cash drawer recount", () => {
  it("places the recount note once between the first and second totals", async () => {
    vi.stubGlobal("matchMedia", () => ({ matches: false, addEventListener() {}, removeEventListener() {} }));
    let releaseCount: (value: unknown) => void = () => undefined;
    const countResult = new Promise((resolve) => { releaseCount = resolve; });
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (String(url).endsWith("/context")) {
        return Promise.resolve({ ok: true, json: async () => ({ shiftOpen: true, denominations: [denomination] }) });
      }
      expect(init?.method).toBe("POST");
      return countResult;
    }));

    render(<CashDrawerScreen session={session} onBack={() => undefined} />);
    const quantity = await screen.findByLabelText("كمية شيكل");
    fireEvent.change(quantity, { target: { value: "4" } });
    fireEvent.click(screen.getByRole("button", { name: "تأكيد العد" }));
    expect((await screen.findByRole("status")).textContent).toContain("جاري حفظ العد");

    releaseCount({
      ok: true,
      json: async () => ({ outcome: "Retry", attempt: 1, status: "Shortage", difference: -1, counted: 4, countId: "first", charged: false, message: sentence }),
    });
    const note = await screen.findByText(sentence);
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.getAllByText(sentence)).toHaveLength(1);
    const stack = note.parentElement;
    const labels = [...stack!.querySelectorAll("span, p")].map((node) => node.textContent);
    expect(labels).toEqual(["العد الأول", sentence, "العد الثاني"]);
  });

  it("splits every denomination into equal rows inside the space above the keypad", async () => {
    vi.stubGlobal("matchMedia", () => ({ matches: false, addEventListener() {}, removeEventListener() {} }));
    const items = ["نصف شيكل", "شيكل", "شيكلان", "خمسمائة شيكل"].map((name, index) => ({
      denominationId: `coin-${index}`,
      unitValue: index + 1,
      displayName: name,
      sortOrder: index,
    }));
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve({
      ok: true,
      json: async () => ({ shiftOpen: true, denominations: items }),
    })));

    const view = render(<CashDrawerScreen session={session} onBack={() => undefined} />);
    await screen.findByLabelText("كمية خمسمائة شيكل");
    const grid = view.container.querySelector(".drawer-grid") as HTMLElement;
    expect(grid.style.gridTemplateColumns).toBe("repeat(3, minmax(0, 1fr))");
    expect(grid.style.gridTemplateRows).toBe("repeat(2, minmax(0, 1fr))");
  });
});
