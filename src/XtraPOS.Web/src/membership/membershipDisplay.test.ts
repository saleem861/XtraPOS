import { describe, expect, it } from "vitest";
import { membershipDisplayUpdate } from "./membershipDisplay";

describe("membershipDisplayUpdate", () => {
  it("keeps a pending QR only for the bound counter", () => {
    const shown = membershipDisplayUpdate({ type: "qr", status: "Pending", url: "https://pos/qr", counterId: "counter-a" }, "counter-a");
    expect(shown).toEqual({ status: "Pending", url: "https://pos/qr" });
    expect(membershipDisplayUpdate({ type: "qr", status: "Pending", url: "https://pos/other", counterId: "counter-b" }, "counter-a")).toBeNull();
  });

  it("clears the QR after success, expiry, or cancellation", () => {
    for (const status of ["Verified", "Expired", "Cancelled"]) {
      expect(membershipDisplayUpdate({ type: "qr", status, url: "https://pos/qr", counterId: "counter-a" }, "counter-a")).toEqual({ status, url: "" });
    }
  });
});