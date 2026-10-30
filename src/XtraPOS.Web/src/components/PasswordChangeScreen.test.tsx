import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { PasswordChangeScreen } from "./PasswordChangeScreen";

afterEach(() => {
  cleanup();
});

describe("password change after temporary login", () => {
  it("shows the password change step", () => {
    render(<PasswordChangeScreen pending={false} error="" onSubmit={() => undefined} />);

    expect(screen.getByRole("heading", { name: "تغيير كلمة المرور" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "حفظ كلمة المرور" })).toBeTruthy();
  });
});
