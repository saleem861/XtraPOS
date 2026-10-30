import { render, screen } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { IdbDebugPanel } from "./IdbDebugPanel";

afterEach(() => {
  window.history.replaceState({}, "", "/");
});

it("stays hidden on the normal development address", () => {
  window.history.replaceState({}, "", "/");
  render(<IdbDebugPanel />);
  expect(screen.queryByText("نسخ السجل")).toBeNull();
});

it("shows the storage log when debug=idb is present", () => {
  window.history.replaceState({}, "", "/?debug=idb");
  render(<IdbDebugPanel />);
  expect(screen.getByText("نسخ السجل")).toBeTruthy();
  expect(screen.getByText("DB: xtrapos-pos")).toBeTruthy();
});
