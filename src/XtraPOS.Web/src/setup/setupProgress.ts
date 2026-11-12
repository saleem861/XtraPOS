import type { BranchSetupInput } from "../types/setup";
import { emptyBranchSetup } from "../types/setup";
import type { WizardStepId } from "./wizardFlow";

const storageKey = "xtrapos-setup-progress";
const secretKeys = ["password", "confirmPassword", "centralPassword", "branchPassword", "remotePassword", "pairingToken"] as const;

export function rememberSetupProgress(draft: BranchSetupInput, step: WizardStepId) {
  const stored: BranchSetupInput = { ...draft };
  for (const key of secretKeys) {
    stored[key] = "";
  }
  sessionStorage.setItem(storageKey, JSON.stringify({ draft: stored, step }));
}

export function readSetupProgress(): { draft: BranchSetupInput; step: WizardStepId } | null {
  const raw = sessionStorage.getItem(storageKey);
  if (!raw) {
    return null;
  }
  try {
    const parsed = JSON.parse(raw) as { draft?: Partial<BranchSetupInput>; step?: WizardStepId };
    if (!parsed.draft?.deviceRole || !parsed.step) {
      return null;
    }
    const draft = { ...emptyBranchSetup, ...parsed.draft };
    for (const key of secretKeys) {
      draft[key] = "";
    }
    return { draft, step: parsed.step };
  } catch {
    return null;
  }
}
