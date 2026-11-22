import { describe, expect, it } from "vitest";
import { monthEnd, monthStart, nextMonth, selectionNet, signedMoney, toggleSelection, type CashErrorItem } from "./cashErrors";

function item(itemId: string, employeeId: string, amount: number, status: CashErrorItem["status"] = "Open"): CashErrorItem {
  return {
    itemId, kind: "Error", employeeId, employeeName: employeeId, branchId: "b", branchName: "Labtop", stationNumber: 1, stationName: "كاونتر 1",
    amount, occurredAt: "2026-11-10T09:00:00", effectiveAt: "2026-11-10T09:00:00", justification: null, decisionCode: null, status,
    closedBy: null, closedType: null, closedMethod: null, closedAt: null, closedByName: null, deferredTo: null, noteCount: 0,
  };
}

describe("cash error helpers", () => {
  it("selects open errors of one employee only and sums the signed net", () => {
    const all = [item("a1", "ahmad", -10), item("a2", "ahmad", 4), item("s1", "sami", -1), item("a3", "ahmad", -2, "Settled")];
    let state = toggleSelection([], all[0], all);
    state = toggleSelection(state.selected, all[1], all);
    expect(state.selected).toEqual(["a1", "a2"]);
    expect(toggleSelection(state.selected, all[2], all).error).toContain("موظف واحد");
    expect(toggleSelection(state.selected, all[3], all).error).toContain("مسوّى");
    expect(selectionNet(state.selected, all)).toBe(-6);
    expect(toggleSelection(state.selected, all[0], all).selected).toEqual(["a2"]);
  });

  it("formats money with a sign and works out month bounds", () => {
    expect(signedMoney(-6)).toBe("-6.00");
    expect(signedMoney(4.5)).toBe("+4.50");
    expect(signedMoney(0)).toBe("0.00");
    const day = new Date(2026, 1, 14);
    expect(monthStart(day)).toBe("2026-02-01");
    expect(monthEnd(day)).toBe("2026-02-28");
    expect(nextMonth(new Date(2026, 11, 3))).toBe("2027-01");
  });
});
