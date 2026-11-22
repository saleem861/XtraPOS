import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { moveItem, quickItemIcon } from "../api/quickItems";
import type { DeviceSession } from "../types/setup";
import { QuickItemsSettingsScreen } from "./QuickItemsSettingsScreen";
import { QuickItemsStrip } from "./QuickItemsStrip";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const session = {
  deviceId: "d1", businessId: "b", branchId: "br", stationId: "s", deviceName: "صندوق 1", deviceType: "POS", deviceToken: "token",
  registeredAt: "2026-01-01", businessName: "المتجر", branchName: "Labtop", branchType: "Retail", stationName: "كاونتر 1", stationNumber: 1,
} as DeviceSession;

const reply = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: async () => body });

describe("QuickItemsStrip", () => {
  it("shows the branch list and adds a tapped item through the normal entry path", async () => {
    vi.stubGlobal("fetch", vi.fn(() => reply({ lines: [
      { itemNumber: "650", itemName: "رأس أرجيلة بلدي حبة", classification: "أراجيل" },
      { itemNumber: "224", itemName: "فحم سريع", classification: null },
    ] })));
    const picked: string[] = [];
    render(<QuickItemsStrip session={session} onPick={(number) => picked.push(number)} />);
    await userEvent.click(await screen.findByRole("button", { name: /650/ }));
    expect(picked).toEqual(["650"]);
    expect(screen.getByRole("button", { name: /224/ })).toBeTruthy();
  });

  it("stays hidden when no list exists", async () => {
    const fetchMock = vi.fn(() => reply({ lines: [] }));
    vi.stubGlobal("fetch", fetchMock);
    const { container } = render(<QuickItemsStrip session={session} onPick={() => undefined} />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(container.querySelector(".quick-items")).toBeNull();
  });
});

describe("QuickItemsSettingsScreen", () => {
  it("opens on all branches, reorders and saves the list", async () => {
    const saved: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (url.startsWith("/api/quick-items/manage") && init?.method === "PUT") {
        saved.push(JSON.parse(String(init.body)));
        return reply({ setId: "x", relayed: true, count: 2, message: "تم حفظ الأصناف السريعة ووصلت إلى الإدارة المركزية، وتصل إلى الفروع خلال دقيقة." });
      }
      if (url.startsWith("/api/quick-items/manage")) {
        return reply({
          source: "central", notice: null, branchId: null, branchName: null, inherited: false, set: null, maxItems: 30,
          options: { branches: [{ id: "b1", name: "Labtop" }, { id: "b1x", name: "Labtop" }, { id: "b2", name: "Sharbel" }] },
          lines: [{ itemNumber: "650", itemName: "رأس أرجيلة", classification: null, active: true }, { itemNumber: "224", itemName: "فحم", classification: null, active: true }],
        });
      }
      return reply({});
    }));

    render(<QuickItemsSettingsScreen session={session} onBack={() => undefined} />);
    const picker = await screen.findByRole("combobox") as HTMLSelectElement;
    expect(picker.value).toBe("");
    expect(screen.getAllByRole("option", { name: "Labtop" })).toHaveLength(1);
    await screen.findByText("650");

    await userEvent.click(screen.getByRole("button", { name: "خفض رأس أرجيلة" }));
    await userEvent.click(screen.getByRole("button", { name: /حفظ/ }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toEqual({ branchId: null, itemNumbers: ["224", "650"] });
  });
});

describe("quick item helpers", () => {
  it("picks an icon by classification or name and moves items", () => {
    expect(quickItemIcon({ classification: "فحم", itemName: "x" })).toBe("🔥");
    expect(quickItemIcon({ classification: null, itemName: "شيء آخر" })).toBe("📦");
    expect(moveItem(["a", "b", "c"], 0, 1)).toEqual(["b", "a", "c"]);
    expect(moveItem(["a", "b"], 0, -1)).toEqual(["a", "b"]);
  });
});
