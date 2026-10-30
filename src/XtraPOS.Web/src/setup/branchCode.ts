import type { BranchSetupInput } from "../types/setup";

export const PREVIEW_BRANCH_CODES = ["01", "02", "03"] as const;

export const BRANCH_CODE_DUPLICATE_MESSAGE = "رمز الفرع مستخدم مسبقًا. اختر رمزًا آخر.";

export const BRANCH_CODE_HELPER = "تم اقتراح الرمز تلقائيًا ويمكن تغييره.";

export function suggestBranchCode(codes: readonly (string | null | undefined)[]): string {
  const used = new Set<number>();
  let width = 0;
  for (const code of codes) {
    const parsed = numericCode(code);
    if (!parsed) {
      continue;
    }
    if (parsed.value > 0) {
      used.add(parsed.value);
    }
    if (parsed.digits.length > 1 && parsed.digits.startsWith("0")) {
      width = Math.max(width, parsed.digits.length);
    }
  }

  let next = 1;
  while (used.has(next)) {
    next += 1;
  }

  if (width === 0) {
    return used.size === 0 ? "01" : String(next);
  }

  const text = String(next);
  return text.length >= width ? text : text.padStart(width, "0");
}

export function isDuplicateBranchCode(
  codes: readonly (string | null | undefined)[],
  candidate: string,
  currentCode = "",
): boolean {
  const wanted = candidate.trim().toLocaleLowerCase();
  if (!wanted) {
    return false;
  }
  const owned = currentCode.trim().toLocaleLowerCase();
  let skippedCurrent = false;
  return codes.some((code) => {
    const text = code?.trim().toLocaleLowerCase() ?? "";
    if (!skippedCurrent && owned && text === owned && text === wanted) {
      skippedCurrent = true;
      return false;
    }
    return text === wanted;
  });
}

export function sqlPort(value: string): number | null {
  const trimmed = value.trim();
  if (!/^[1-9]\d{0,4}$/.test(trimmed)) {
    return null;
  }
  const port = Number(trimmed);
  return port <= 65535 ? port : null;
}

export function branchCodeRequest(draft: BranchSetupInput, branchCode?: string) {
  const central = draft.operatingMode === "CentralManagement";
  return {
    source: central ? "Central" : "Branch",
    intent: central ? draft.centralIntent || "new" : draft.branchIntent || "new",
    locationKind: central ? draft.centralLocation || "Local" : draft.branchLocation || "Local",
    server: draft.centralServer.trim() || "localhost",
    instanceName: draft.centralInstance.trim() || null,
    port: sqlPort(draft.centralPort),
    databaseName: draft.centralDatabaseName.trim() || null,
    authenticationType: draft.centralUsername.trim() ? "Sql" : "Integrated",
    username: draft.centralUsername.trim() || null,
    password: draft.centralPassword || null,
    businessName: draft.businessName.trim() || null,
    branchCode: branchCode?.trim() || null,
    branchId: draft.branchIntent === "existing" ? draft.attachedBranchId.trim() || null : null,
  };
}

function numericCode(code: string | null | undefined): { value: number; digits: string } | null {
  const digits = code?.trim() ?? "";
  if (!/^\d{1,9}$/.test(digits)) {
    return null;
  }
  return { value: Number(digits), digits };
}
