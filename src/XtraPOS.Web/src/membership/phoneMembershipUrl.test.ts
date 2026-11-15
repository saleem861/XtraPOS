import { describe, expect, it } from "vitest";
import { phoneMembershipUrl } from "./phoneMembershipUrl";

describe("phone membership links", () => {
  it("rejects the cashier computer and keeps the public site", () => {
    expect(phoneMembershipUrl("http://127.0.0.1:47335/membership/enroll/token")).toBe("https://test-pos.sharbelfashion.com/membership/enroll/token");
    expect(phoneMembershipUrl("http://localhost:47335/membership/enroll/token")).toBe("https://test-pos.sharbelfashion.com/membership/enroll/token");
    expect(phoneMembershipUrl("https://test-pos.sharbelfashion.com/membership/enroll/token")).toBe("https://test-pos.sharbelfashion.com/membership/enroll/token");
  });
});
