import { describe, expect, it } from "vitest";
import { suggestDeviceName } from "./deviceName";

describe("device name suggestion", () => {
  it("uses a host label when the browser can provide one", () => {
    expect(suggestDeviceName("Desktop1")).toBe("Desktop1");
  });

  it("leaves the name empty when the host context is only the server or the operating system", () => {
    expect(suggestDeviceName("")).toBe("");
    expect(suggestDeviceName("localhost")).toBe("");
    expect(suggestDeviceName("Windows")).toBe("");
  });
});
