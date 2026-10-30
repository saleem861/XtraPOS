import { describe, expect, it } from "vitest";
import { creditActivationError, creditStatusState, creditSwitchState } from "./creditPolicyForm";

describe("تفعيل استدانة الزبون", () => {
  it("turns the account active only when the switch is on", () => {
    expect(creditSwitchState(true, "Inactive")).toEqual({ enabled: true, status: "Active" });
    expect(creditSwitchState(true, "Suspended")).toEqual({ enabled: true, status: "Active" });
    expect(creditSwitchState(false, "Active")).toEqual({ enabled: false, status: "Inactive" });
    expect(creditSwitchState(false, "Suspended")).toEqual({ enabled: false, status: "Suspended" });
  });

  it("keeps the status list aligned with the switch", () => {
    expect(creditStatusState("Active")).toEqual({ enabled: true, status: "Active" });
    expect(creditStatusState("Inactive")).toEqual({ enabled: false, status: "Inactive" });
    expect(creditStatusState("Suspended")).toEqual({ enabled: false, status: "Suspended" });
    expect(creditStatusState("Other")).toBeNull();
  });

  it("keeps a turned-on account activatable even before a limit is entered", () => {
    expect(creditActivationError({ enabled: true, limit: "0", paymentTermDays: "30", status: "Active" })).toBeNull();
    expect(creditActivationError({ enabled: true, limit: "", paymentTermDays: "30", status: "Active" })).toBeNull();
    expect(creditActivationError({ enabled: true, limit: "10.12345", paymentTermDays: "30", status: "Active" })).toContain("الحد الأقصى");
    expect(creditActivationError({ enabled: true, limit: "100", paymentTermDays: "", status: "Active" })).toContain("مدة السداد");
    expect(creditActivationError({ enabled: true, limit: "100.5", paymentTermDays: "15", status: "Active" })).toBeNull();
    expect(creditActivationError({ enabled: false, limit: "0", paymentTermDays: "30", status: "Inactive" })).toBeNull();
    expect(creditActivationError({ enabled: false, limit: "100", paymentTermDays: "4000", status: "Suspended" })).toContain("مدة السداد");
  });
});