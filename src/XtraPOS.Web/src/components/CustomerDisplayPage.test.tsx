import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { customerDisplayChannel } from "../customerDisplay/customerDisplay";
import { CustomerDisplayPage } from "./CustomerDisplayPage";

describe("customer display page", () => {
  afterEach(() => cleanup());

  it("follows the sale sent by the POS window", async () => {
    render(<CustomerDisplayPage />);
    expect(screen.getByText("أهلًا وسهلًا")).toBeTruthy();
    const pos = new BroadcastChannel(customerDisplayChannel);
    pos.postMessage({ type: "state", state: {
      phase: "sale", business: "Smoker World", branch: "Labtop", customerName: "test5000",
      lines: [{ id: "1", name: "ولاعة غاز", quantity: "2", unitPrice: "6.00", lineTotal: "12.00" }],
      lastLineId: "1", pieces: "2", beforeDiscount: "12.00", discount: "0.00", total: "12.00",
    } });
    expect(await screen.findByText("أهلًا test5000")).toBeTruthy();
    expect(screen.getAllByText("ولاعة غاز").length).toBe(2);
    expect(screen.getByText("المطلوب")).toBeTruthy();
    expect(screen.queryByText("الخصم")).toBeNull();
    pos.postMessage({ type: "state", state: { phase: "payment", business: "", branch: "", customerName: null, total: "12.00", paid: "20.00", change: "8.00", method: "Cash" } });
    expect(await screen.findByText("الباقي لك")).toBeTruthy();
    expect(screen.getByText("8.00")).toBeTruthy();
    pos.close();
  });
});
