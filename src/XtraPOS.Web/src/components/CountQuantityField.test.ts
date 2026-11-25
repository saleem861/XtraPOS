import { describe, expect, it } from "vitest";
import { applyPadKey } from "./CountQuantityField";

describe("count quantity pad (laptops)", () => {
  it("types digits, backspace, clear, steps and enter like the sales quantity pad", () => {
    expect(applyPadKey("", "7")).toBe("7");
    expect(applyPadKey("0", "5")).toBe("5");
    expect(applyPadKey("12", "BS")).toBe("1");
    expect(applyPadKey("12", "Del")).toBe("");
    expect(applyPadKey("", ".")).toBe("0.");
    expect(applyPadKey("1.5", ".")).toBe("1.5");
    expect(applyPadKey("2", "+")).toBe("3");
    expect(applyPadKey("0", "-")).toBe("0");
    expect(applyPadKey("4", "Enter")).toBe("enter");
    expect(applyPadKey("4", "*")).toBe("4");
  });
});
