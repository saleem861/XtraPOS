import { describe, expect, it } from "vitest";
import { BACKGROUND_HEADER, isBackgroundRequest } from "./waitingActivity";

// تشخيص 24 نوفمبر: استطلاع رمز العضوية كل ثانية كان يُعدّ انتظاراً بعد أي ضغطة، فيبقى «يرجى الانتظار» ظاهراً.
describe("background requests are never shown as user waiting", () => {
  it("treats the membership counter poll and health checks as background", () => {
    expect(isBackgroundRequest("/api/pos/membership-counter/result")).toBe(true);
    expect(isBackgroundRequest("/api/pos/membership-counter/watch", { method: "POST" })).toBe(true);
    expect(isBackgroundRequest("/health")).toBe(true);
  });

  it("honours the background header and leaves screen requests counted", () => {
    expect(isBackgroundRequest("/api/inventory/branches", { headers: { [BACKGROUND_HEADER]: "1" } })).toBe(true);
    expect(isBackgroundRequest("/api/inventory/branches")).toBe(false);
    expect(isBackgroundRequest("/api/inventory/configuration", { headers: { Authorization: "x" } })).toBe(false);
  });
});
