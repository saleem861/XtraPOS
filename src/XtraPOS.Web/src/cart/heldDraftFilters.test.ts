import { describe, expect, it } from "vitest";
import { dateRangeError, draftRange, hebronToday, shiftDate } from "./heldDraftFilters";

describe("held draft filters", () => {
  it("uses the Asia/Hebron calendar day", () => {
    expect(hebronToday(new Date("2026-10-26T22:30:00Z"))).toBe("2026-10-27");
    expect(hebronToday(new Date("2026-10-26T10:00:00Z"))).toBe("2026-10-26");
  });

  it("builds quick ranges and rejects an inverted range", () => {
    expect(draftRange("today", "2026-10-26")).toEqual({ from: "2026-10-26", to: "2026-10-26" });
    expect(draftRange("yesterday", "2026-10-26")).toEqual({ from: "2026-10-25", to: "2026-10-25" });
    expect(draftRange("week", "2026-10-26").from).toBe(shiftDate("2026-10-26", -6));
    expect(draftRange("month", "2026-10-26").from).toBe(shiftDate("2026-10-26", -29));
    expect(dateRangeError("2026-10-26", "2026-10-20")).toBe("تاريخ النهاية أسبق من تاريخ البداية.");
    expect(dateRangeError("2026-10-01", "2026-10-26")).toBeNull();
  });
});
