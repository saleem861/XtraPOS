import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { SETUP_STEPS, type SetupStepId, type SetupStepState } from "../types/setup";
import { SetupProgressScreen } from "./SetupProgressScreen";

afterEach(cleanup);
const steps = Object.fromEntries(SETUP_STEPS.map((step) => [step.id, "pending"])) as Record<SetupStepId, SetupStepState>;

it.each([false, true])("opens an existing branch without offering deletion (legacy response: %s)", async (legacy) => {
  const onRefresh = vi.fn();
  const onRetry = vi.fn();
  render(<SetupProgressScreen steps={steps} running={false}
    error={legacy ? "تم إعداد هذا الفرع من قبل." : ""}
    existingBranch={legacy ? null : { name: "الفرع", code: "02" }}
    onRefresh={onRefresh} onRetry={onRetry} onEdit={vi.fn()} />);

  expect(screen.queryByRole("button", { name: "استبدال الفرع" })).toBeNull();
  expect(screen.getByText(/بياناته محفوظة ولن تُحذف/)).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "فتح الفرع الموجود" }));
  expect(onRefresh).toHaveBeenCalledOnce();
  expect(onRetry).not.toHaveBeenCalled();
});

it("allows retrying an ordinary setup failure", async () => {
  const onRetry = vi.fn();
  render(<SetupProgressScreen steps={steps} running={false} error="تعذر الاتصال"
    existingBranch={null} onRefresh={vi.fn()} onRetry={onRetry} onEdit={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: "إعادة المحاولة" }));
  expect(onRetry).toHaveBeenCalledOnce();
  expect(screen.queryByRole("button", { name: "فتح الفرع الموجود" })).toBeNull();
});
