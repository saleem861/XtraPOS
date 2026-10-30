import type { BranchSetupInput } from "../types/setup";

export function centralSetupPayload(input: BranchSetupInput) {
  const remote = input.centralLocation === "Remote";
  const username = remote ? input.centralUsername.trim() : "";
  return {
    intent: input.centralIntent,
    locationKind: input.centralLocation,
    server: remote ? input.centralServer.trim() : null,
    instanceName: remote ? input.centralInstance.trim() || null : null,
    port: remote && /^\d+$/.test(input.centralPort.trim()) ? Number(input.centralPort.trim()) : null,
    databaseName: input.centralDatabaseName.trim(),
    authenticationType: remote && username ? "Sql" : "Integrated",
    username: remote && username ? username : null,
    password: remote && username ? input.centralPassword : null,
    encrypt: input.centralEncrypt,
    trustServerCertificate: input.centralTrustServerCertificate,
    businessName: input.centralIntent === "new" ? input.businessName.trim() : null,
    businessDisplayName: input.centralIntent === "new" ? input.businessDisplayName.trim() || null : null,
    description: input.centralIntent === "new" ? input.address.trim() || null : null,
  };
}
