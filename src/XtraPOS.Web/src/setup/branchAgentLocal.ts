export const branchAgentPort = 47331;
export const branchAgentProduct = "XtraPOS.BranchAgent";

export interface LocalAgentStatus {
  product: string;
  version: string;
  machineName: string;
  paired: boolean;
  server: string;
  agentId?: string | null;
  instances: string[];
  sqlAvailable: boolean;
}

export type BranchAgentState =
  | "missing"
  | "outdated"
  | "sql-missing"
  | "unpaired"
  | "other-environment"
  | "ready";

export function normalizeServer(server: string): string {
  return server.trim().replace(/\/+$/, "");
}

export function decideBranchAgent(status: LocalAgentStatus | null, expectedServer: string, expectedVersion: string): BranchAgentState {
  if (!status || status.product !== branchAgentProduct) {
    return "missing";
  }
  if (compareVersions(status.version, expectedVersion) < 0) {
    return "outdated";
  }
  if (!status.sqlAvailable) {
    return "sql-missing";
  }
  if (!status.paired || !status.agentId) {
    return "unpaired";
  }
  if (normalizeServer(status.server) !== normalizeServer(expectedServer)) {
    return "other-environment";
  }
  return "ready";
}

export function compareVersions(actual: string, expected: string): number {
  const left = actual.split(".").map((part) => Number(part) || 0);
  const right = expected.split(".").map((part) => Number(part) || 0);
  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index += 1) {
    const difference = (left[index] ?? 0) - (right[index] ?? 0);
    if (difference !== 0) {
      return difference;
    }
  }
  return 0;
}

export function localAgentUrl(path: string): string {
  return `http://127.0.0.1:${branchAgentPort}${path}`;
}
