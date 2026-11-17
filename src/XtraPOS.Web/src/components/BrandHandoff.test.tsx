import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { BrandHandoff, hasSetupHandoff, markSetupHandoff, clearSetupHandoff } from "./BrandHandoff";

afterEach(() => {
  cleanup();
  sessionStorage.clear();
  vi.unstubAllGlobals();
});

describe("setup handoff", () => {
  it("shows the moving brand until login is ready", async () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise(() => undefined)));
    markSetupHandoff();
    render(<BrandHandoff onReady={() => undefined} onGiveUp={() => undefined} />);
    expect(hasSetupHandoff()).toBe(true);
    expect(await screen.findByText("إكسترا بوس")).toBeTruthy();
    expect(screen.getByText("XtraPOS")).toBeTruthy();
    clearSetupHandoff();
    expect(hasSetupHandoff()).toBe(false);
  });
});
