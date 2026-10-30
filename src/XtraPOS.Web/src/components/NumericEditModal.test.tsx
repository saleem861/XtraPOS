import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { NumericEditModal } from "./NumericEditModal";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function stubPointer(fine: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: query.includes("(pointer: fine)") ? fine : false,
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
  }));
}

it("shows the internal keypad for a fine pointer and replaces the first digit", async () => {
  stubPointer(true);
  const cashier = userEvent.setup();
  const onConfirm = vi.fn();
  render(<NumericEditModal title="الكمية" ariaLabel="تعديل الكمية" inputLabel="الكمية" initialValue="3" allowDecimal={false} error="" onConfirm={onConfirm} onCancel={() => undefined} />);
  expect(screen.getByRole("button", { name: "5" })).toBeTruthy();
  expect((screen.getByLabelText("الكمية") as HTMLInputElement).readOnly).toBe(true);
  expect((screen.getByLabelText("الكمية") as HTMLInputElement).inputMode).toBe("none");
  await cashier.keyboard("5");
  expect((screen.getByLabelText("الكمية") as HTMLInputElement).value).toBe("5");
  await cashier.keyboard("2");
  expect((screen.getByLabelText("الكمية") as HTMLInputElement).value).toBe("52");
  await cashier.keyboard("{Enter}");
  expect(onConfirm).toHaveBeenCalledWith("52");
});

it("hides the internal keypad for a coarse pointer and requests a native numeric keyboard", () => {
  stubPointer(false);
  render(<NumericEditModal title="سعر الوحدة" ariaLabel="تعديل سعر الوحدة" inputLabel="السعر" initialValue="25.5" allowDecimal error="" onConfirm={() => undefined} onCancel={() => undefined} />);
  expect(screen.queryByRole("button", { name: "5" })).toBeNull();
  expect((screen.getByLabelText("السعر") as HTMLInputElement).readOnly).toBe(false);
  expect((screen.getByLabelText("السعر") as HTMLInputElement).inputMode).toBe("decimal");
  expect(document.querySelector(".pos-overlay-top")).toBeTruthy();
});
