import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { TemporaryPasswordDialog } from "./UserManagementScreen";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function Harness() {
  const [open, setOpen] = useState(true);
  if (!open) {
    return <p>تم الإغلاق</p>;
  }

  return (
    <TemporaryPasswordDialog
      secret={{ username: "emp.test", fullName: "مستخدم تجربة", password: "Temp-Once-6h" }}
      onClose={() => setOpen(false)}
    />
  );
}

describe("temporary password dialog", () => {
  it("shows the password once and does not store it", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    render(<Harness />);

    expect(screen.getByRole("dialog", { name: "تم إنشاء كلمة مرور مؤقتة" })).toBeTruthy();
    expect(screen.getByText("emp.test")).toBeTruthy();
    expect(screen.getByText("مستخدم تجربة")).toBeTruthy();
    expect(screen.getByText("Temp-Once-6h")).toBeTruthy();

    await userEvent.click(screen.getByRole("button", { name: "نسخ كلمة المرور" }));
    expect(writeText).toHaveBeenCalledWith("Temp-Once-6h");

    await userEvent.click(screen.getByRole("button", { name: "إغلاق" }));
    expect(screen.getByText("تم الإغلاق")).toBeTruthy();
    expect(screen.queryByText("Temp-Once-6h")).toBeNull();
    expect(setItem).not.toHaveBeenCalled();
  });
});
