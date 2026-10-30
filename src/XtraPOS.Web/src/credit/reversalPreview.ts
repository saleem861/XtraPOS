export function shownReversalDebt(input: { outstanding: number; expectedOutstanding: number; state: string; reason: "RefundToCustomer" | "IncorrectRecording" }) {
  const posted = input.state === "Completed";
  return {
    reason: input.reason,
    current: input.outstanding,
    after: posted ? input.outstanding : input.expectedOutstanding,
    confirmed: posted,
  };
}
