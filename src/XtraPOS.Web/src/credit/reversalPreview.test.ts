import { describe, expect, it } from "vitest";
import { shownReversalDebt } from "./reversalPreview";

describe("reversal preview", () => {
  it("keeps the server expected debt for a refund and does not add it again after completion", () => {
    expect(shownReversalDebt({ outstanding: 500, expectedOutstanding: 800, state: "Eligible", reason: "RefundToCustomer" })).toMatchObject({ current: 500, after: 800, confirmed: false });
    expect(shownReversalDebt({ outstanding: 800, expectedOutstanding: 1100, state: "Completed", reason: "RefundToCustomer" })).toMatchObject({ current: 800, after: 800, confirmed: true });
  });

  it("keeps the server expected debt for an incorrect recording and does not add it again after completion", () => {
    expect(shownReversalDebt({ outstanding: 500, expectedOutstanding: 800, state: "Eligible", reason: "IncorrectRecording" })).toMatchObject({ current: 500, after: 800, confirmed: false });
    expect(shownReversalDebt({ outstanding: 800, expectedOutstanding: 1100, state: "Completed", reason: "IncorrectRecording" })).toMatchObject({ current: 800, after: 800, confirmed: true });
  });
});
