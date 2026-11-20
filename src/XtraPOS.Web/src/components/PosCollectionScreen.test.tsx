import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DeviceSession } from "../types/setup";
import { PosCollectionScreen } from "./PosCollectionScreen";

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

describe("pos debt collection", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("reloads the debt history right after a successful repayment", async () => {
    const repayment = { operationId: "op-1", eventType: "Repayment", amount: 10, signedEffect: -10, balanceAfter: 11, confirmedAt: "2026-11-21T00:00:00Z", branchName: "Labtop", cashier: "سليم" };
    let paid = false;
    const historyCalls: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      const reply = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: async () => body });
      if (url.includes("/api/pos/credit-history")) {
        historyCalls.push(url);
        return reply({ movements: paid ? [repayment] : [], debtTotal: 21, repaymentTotal: paid ? 10 : 0, confirmedOutstanding: paid ? 11 : 21, stale: false });
      }
      if (url.includes("/api/pos/credit-collections/quote")) return reply({ error: null });
      if (url.endsWith("/api/pos/credit-collections")) {
        paid = true;
        return reply({ state: "Completed" });
      }
      return reply({});
    }));

    render(<PosCollectionScreen session={session} initialCustomerId="customer-1" initialNumber="0592594777" initialName="test5000" cashSessionId="session-1" onBack={() => undefined} />);
    await waitFor(() => expect(historyCalls.length).toBe(1));
    fireEvent.change(screen.getByLabelText("مبلغ التسديد"), { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: "تسديد" }));

    expect(await screen.findByText("تم تسديد الدين بنجاح")).toBeTruthy();
    await waitFor(() => expect(historyCalls.length).toBe(2));
    expect(await screen.findByText("Labtop")).toBeTruthy();
  });
});
