import type { BranchSetupInput } from "../types/setup";

export const PREVIEW_TAKEN_BRANCH_DATABASES = ["XtraPOS_Branch", "XtraPOS_Branch_Test_Ramtha"] as const;

export function isSafeDatabaseIdentifier(name: string): boolean {
  return /^[A-Za-z][A-Za-z0-9_]{0,127}$/.test(name.trim());
}

export function isCentralCatalogName(name: string): boolean {
  const trimmed = name.trim();
  return trimmed === "XtraPOS_Central" || trimmed.startsWith("XtraPOS_Central_");
}

export function suggestNewBranchDatabaseName(branchCode: string): string {
  const code = branchCode.trim();
  if (!/^\d{1,9}$/.test(code)) {
    return "XtraPOS_Branch";
  }
  const padded = code.length >= 2 ? code : code.padStart(2, "0");
  return `XtraPOS_Branch_${padded}`;
}

export function branchDatabaseNameReady(name: string, taken: readonly string[] = []): boolean {
  const trimmed = name.trim();
  if (!isSafeDatabaseIdentifier(trimmed) || isCentralCatalogName(trimmed)) {
    return false;
  }
  return !taken.some((item) => item.trim().toLocaleLowerCase() === trimmed.toLocaleLowerCase());
}

export function connectionInputsReady(input: {
  remote: boolean;
  host: string;
  databaseName: string;
  authentication: "" | "Integrated" | "Sql";
  username: string;
  password: string;
  rejectCentralName?: boolean;
}): boolean {
  if (!isSafeDatabaseIdentifier(input.databaseName)) {
    return false;
  }
  if (input.rejectCentralName && isCentralCatalogName(input.databaseName)) {
    return false;
  }
  if (input.remote && input.host.trim().length === 0) {
    return false;
  }
  if (input.authentication === "Sql" && (input.username.trim().length === 0 || input.password.length === 0)) {
    return false;
  }
  return true;
}

export function displayedServer(location: string, server: string): string {
  if (location === "Remote") {
    return server.trim();
  }
  return server.trim() || "localhost";
}

export function connectionContextKey(draft: BranchSetupInput, target: "central" | "branch" | "remote"): string {
  const central = [
    draft.deviceRole,
    draft.operatingMode,
    draft.centralIntent,
    draft.centralLocation,
    draft.centralDatabaseName.trim(),
    draft.centralLocation === "Remote" ? draft.centralServer.trim() : "local",
    draft.centralPort.trim(),
    draft.centralInstance.trim(),
    draft.centralAuthentication === "Sql" ? `sql:${draft.centralUsername.trim()}:${draft.centralPassword}` : "integrated",
  ];
  if (target === "central") {
    return ["central", ...central].join("|");
  }

  const branch = [
    ...central,
    draft.businessName.trim(),
    draft.branchIntent,
    draft.branchLocation,
    draft.branchDatabaseName.trim(),
    draft.attachedBranchId.trim(),
    draft.branchLocation === "Remote" ? draft.branchServer.trim() : "local",
    draft.branchPort.trim(),
    draft.branchInstance.trim(),
    draft.branchAuthentication === "Sql" ? `sql:${draft.branchUsername.trim()}:${draft.branchPassword}` : "integrated",
  ];
  if (target === "branch") {
    return ["branch", ...branch].join("|");
  }

  return [
    "remote",
    ...branch,
    draft.branchRemoteDatabaseName.trim(),
    draft.remoteServer.trim(),
    draft.remotePort.trim(),
    draft.remoteInstance.trim(),
    draft.remoteAuthentication === "Sql" ? `sql:${draft.remoteUsername.trim()}:${draft.remotePassword}` : "integrated",
  ].join("|");
}

export function withConnectionVerified(
  draft: BranchSetupInput,
  target: "central" | "branch" | "remote",
  patch: Partial<BranchSetupInput> = {},
): BranchSetupInput {
  const next = { ...draft, ...patch };
  const key = connectionContextKey(next, target);
  if (target === "central") {
    return { ...next, centralConnectionTested: true, centralVerifiedKey: key };
  }
  if (target === "branch") {
    return { ...next, branchConnectionTested: true, branchVerifiedKey: key };
  }
  return { ...next, remoteCopyTested: true, remoteVerifiedKey: key };
}

export function connectionVerified(draft: BranchSetupInput, target: "central" | "branch" | "remote"): boolean {
  const key = connectionContextKey(draft, target);
  if (!key) {
    return false;
  }
  if (target === "central") {
    return draft.centralConnectionTested && draft.centralVerifiedKey === key;
  }
  if (target === "branch") {
    return draft.branchConnectionTested && draft.branchVerifiedKey === key;
  }
  return draft.remoteCopyTested && draft.remoteVerifiedKey === key;
}

export function clearedBranchIdentity(): Partial<BranchSetupInput> {
  return {
    selectedBranchId: "",
    attachedBranchId: "",
    attachedBranchCode: "",
    branchName: "",
    branchCode: "",
    branchType: "",
    branchDatabaseName: "",
    branchConnectionTested: false,
    branchVerifiedKey: "",
    branchManualConnection: false,
    branchServer: "",
    branchPort: "",
    branchInstance: "",
    branchAuthentication: "Integrated",
    branchUsername: "",
    branchPassword: "",
    branchUseCentralServer: false,
    remoteCopyTested: false,
    remoteVerifiedKey: "",
    remoteServer: "",
    remotePort: "",
    remoteInstance: "",
    remoteAuthentication: "Integrated",
    remoteUsername: "",
    remotePassword: "",
    branchRemoteDatabaseName: "",
  };
}
