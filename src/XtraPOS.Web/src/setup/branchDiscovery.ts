export interface BranchDatabaseChoice {
  branchId?: string;
  branchName: string;
  branchCode: string;
  databaseName: string;
  businessName: string;
  server: string;
  role?: string;
}

export function acceptedLocalBranchDatabases(
  found: readonly BranchDatabaseChoice[],
  centralBusinessName = "",
): BranchDatabaseChoice[] {
  const business = centralBusinessName.trim();
  return found.filter((item) => {
    const databaseName = item.databaseName.trim();
    const role = (item.role ?? "Branch").trim();
    if (role !== "Branch") {
      return false;
    }
    if (!databaseName || databaseName === "XtraPOS_Central" || databaseName.startsWith("XtraPOS_Central")) {
      return false;
    }
    if (!item.branchName.trim()) {
      return false;
    }
    if (business && item.businessName.trim() !== business) {
      return false;
    }
    return true;
  });
}
