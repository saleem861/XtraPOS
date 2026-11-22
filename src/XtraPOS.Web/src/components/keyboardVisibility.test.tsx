import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { KeyboardToggle, setFullKeyboardVisible, useFullKeyboardVisible } from "./keyboardVisibility";

function Screen({ name }: { name: string }) {
  const [open] = useFullKeyboardVisible();
  return <p>{name}: {open ? "ظاهرة" : "مخفية"}</p>;
}

afterEach(() => {
  cleanup();
  act(() => setFullKeyboardVisible(true));
});

describe("shared full keyboard visibility", () => {
  it("hides and shows the keyboard on every screen at once and remembers the choice", () => {
    render(<><Screen name="الجرد" /><Screen name="الزبائن" /><KeyboardToggle /></>);
    expect(screen.getByText("الجرد: ظاهرة")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "إخفاء لوحة المفاتيح والأرقام" }));
    expect(screen.getByText("الجرد: مخفية")).toBeTruthy();
    expect(screen.getByText("الزبائن: مخفية")).toBeTruthy();
    expect(window.localStorage.getItem("xtrapos.fullKeyboardVisible")).toBe("0");

    cleanup();
    render(<Screen name="المبيعات" />);
    expect(screen.getByText("المبيعات: مخفية")).toBeTruthy();
  });
});
