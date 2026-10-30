import { SetupClientError } from "../api/client";
import type { BranchSetupInput } from "../types/setup";

export interface CentralAdminChoice {
  userId: string;
  username: string;
  displayName: string;
  fullName: string | null;
  employeeNumber: string | null;
  roleCode: string;
  roleName: string;
}

export async function listCentralAdmins(draft: BranchSetupInput): Promise<CentralAdminChoice[]> {
  const response = await fetch("/api/setup/central-admins", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({
      databaseName: draft.centralDatabaseName.trim(),
      server: draft.centralServer.trim() || "localhost",
      instanceName: draft.centralInstance.trim() || null,
      port: draft.centralPort.trim() ? Number(draft.centralPort) : null,
      authenticationType: draft.centralAuthentication || "Integrated",
      username: draft.centralUsername.trim() || null,
      password: draft.centralPassword || null,
      expectedRole: "Central",
    }),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر قراءة مديري الإدارة المركزية."));
  }

  const body = (await response.json()) as { admins?: CentralAdminChoice[] };
  if (!Array.isArray(body.admins)) {
    throw new SetupClientError("تعذر قراءة مديري الإدارة المركزية.");
  }

  return body.admins;
}

export async function verifyCentralAdmin(draft: BranchSetupInput): Promise<CentralAdminChoice> {
  const response = await fetch("/api/setup/verify-central-admin", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({
      databaseName: draft.centralDatabaseName.trim(),
      server: draft.centralServer.trim() || "localhost",
      instanceName: draft.centralInstance.trim() || null,
      port: draft.centralPort.trim() ? Number(draft.centralPort) : null,
      authenticationType: draft.centralAuthentication || "Integrated",
      username: draft.centralUsername.trim() || null,
      password: draft.centralPassword || null,
      adminUsername: draft.username.trim(),
      adminPassword: draft.password,
      expectedRole: "Central",
    }),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر التحقق من حساب المدير."));
  }

  const body = (await response.json()) as { admin?: CentralAdminChoice };
  if (!body.admin?.userId) {
    throw new SetupClientError("تعذر التحقق من حساب المدير.");
  }

  return body.admin;
}

async function readMessage(response: Response, fallback: string): Promise<string> {
  try {
    const payload = (await response.json()) as { message?: string };
    return payload.message?.trim() || fallback;
  } catch {
    return fallback;
  }
}
