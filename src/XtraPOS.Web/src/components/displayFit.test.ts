import { describe, expect, it } from "vitest";
import { computeFit, nextScale, resolveScale, scaleLabel } from "./displayFit";

describe("desktop display fit (owner 27 Nov)", () => {
  it("keeps full size when the screen holds the reference layout", () => {
    expect(computeFit(1920, 950)).toBe(1);
    expect(computeFit(2560, 1200)).toBe(1);
  });

  it("shrinks like the browser zoom when the usable space is smaller", () => {
    // 1920×1080 بتكبير ويندوز 125%: مساحة ~1536×730.
    expect(computeFit(1536, 730)).toBe(0.81);
    // لابتوب 1366×768: مساحة ~1366×650.
    expect(computeFit(1366, 650)).toBe(0.72);
    expect(computeFit(800, 400)).toBe(0.6);
    expect(computeFit(0, 0)).toBe(1);
  });

  it("lets a device pin a fixed size and cycles through the choices", () => {
    expect(resolveScale("0.75", 1920, 950)).toBe(0.75);
    expect(resolveScale("auto", 1536, 730)).toBe(0.81);
    expect(nextScale("auto")).toBe("1");
    expect(nextScale("0.75")).toBe("auto");
    expect(scaleLabel("auto", 0.81)).toBe("تلقائي (81%)");
    expect(scaleLabel("0.9", 0.9)).toBe("90%");
  });
});
