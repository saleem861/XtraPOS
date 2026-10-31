import { asDeviceSession } from "../storage/deviceSession";
import type {
  AdminSetupInput,
  BranchSetupInput,
  CurrentUser,
  DeviceSession,
  SetupStatusResponse,
  SetupStreamEvent,
} from "../types/setup";

export class SetupClientError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SetupClientError";
  }
}

export function deviceHeaders(session: DeviceSession): HeadersInit {
  return {
    "X-Device-Id": session.deviceId,
    "X-Device-Token": session.deviceToken,
  };
}

function adminPayload(input: AdminSetupInput | BranchSetupInput) {
  if ("centralAdminUserId" in input && input.centralAdminUserId.trim()) {
    return {
      username: input.username.trim(),
      displayName: input.displayName.trim(),
      fullName: input.fullName.trim() || null,
      employeeNumber: input.employeeNumber.trim() || null,
      password: "",
      confirmPassword: "",
    };
  }

  return {
    username: input.username.trim(),
    displayName: input.displayName.trim(),
    fullName: input.fullName.trim() || null,
    employeeNumber: input.employeeNumber.trim() || null,
    password: input.password,
    confirmPassword: input.confirmPassword,
  };
}

async function readMessage(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { message?: string };
    return body.message?.trim() || fallback;
  } catch {
    return fallback;
  }
}

export interface LocalRecoveryStation {
  stationId: string;
  stationNumber: number;
  stationName: string;
}

export interface LocalRecoveryContext {
  businessName: string;
  businessDisplayName?: string | null;
  branchId: string;
  branchName: string;
  branchCode?: string | null;
  branchType: DeviceSession["branchType"];
  suggestedDeviceName: string;
  existingDeviceName?: string | null;
  existingDeviceId?: string | null;
  stations: LocalRecoveryStation[];
}

export async function listBranchDatabases(): Promise<BranchDatabaseCandidate[]> {
  const response = await fetch("/api/setup/branch-databases");
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر قراءة الفروع."));
  }

  const body = (await response.json()) as { databases: BranchDatabaseCandidate[] };
  return body.databases;
}

export interface BranchDatabaseCandidate {
  branchName: string;
  branchCode: string;
  databaseName: string;
  businessName: string;
  server: string;
}

export async function getLocalDeviceRecovery(databaseName?: string): Promise<LocalRecoveryContext> {
  const response = await fetch("/api/setup/local-device-recovery", {
    headers: databaseName ? { "X-Setup-Branch-Database": databaseName } : undefined,
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر قراءة الفرع الحالي."));
  }

  return (await response.json()) as LocalRecoveryContext;
}

export async function recoverMainDevice(input: {
  username: string;
  password: string;
  stationId?: string;
  stationNumber?: number;
  stationName?: string;
  deviceName: string;
  databaseName?: string;
}): Promise<DeviceSession> {
  const response = await fetch("/api/setup/recover-main-device", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(input.databaseName ? { "X-Setup-Branch-Database": input.databaseName } : {}),
    },
    body: JSON.stringify(input),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر ربط هذا الجهاز."));
  }

  const session = asDeviceSession(await response.json());
  if (!session) {
    throw new SetupClientError("تعذر حفظ تسجيل الجهاز على هذا الجهاز.");
  }

  return session;
}

export async function createTestBranch(input: {
  adminUsername: string;
  adminPassword: string;
  adminDisplayName: string;
  branchName: string;
  branchCode: string;
  deviceName: string;
  linkToCentral?: boolean;
  centralUsername?: string;
  centralPassword?: string;
  onboardingAuthorization?: string;
}): Promise<DeviceSession> {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (input.onboardingAuthorization) {
    headers["X-Onboarding-Authorization"] = input.onboardingAuthorization;
  }

  const response = await fetch("/api/setup/new-test-branch", {
    method: "POST",
    headers,
    body: JSON.stringify(input),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر إنشاء الفرع."));
  }

  const session = asDeviceSession(await response.json());
  if (!session) {
    throw new SetupClientError("تعذر حفظ تسجيل الجهاز على هذا الجهاز.");
  }

  return session;
}

export async function getSetupStatus(): Promise<SetupStatusResponse> {
  const response = await fetch("/api/setup/status");
  if (!response.ok) {
    throw new SetupClientError("تعذر التحقق من الإعداد.");
  }

  return (await response.json()) as SetupStatusResponse;
}

export async function createBranch(
  input: BranchSetupInput,
  onEvent: (event: SetupStreamEvent) => void,
  authorization = "",
): Promise<void> {
  const response = await fetch("/api/setup/create-branch", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/x-ndjson",
      "X-Setup-Authorization": authorization,
    },
    body: JSON.stringify({
      businessName: input.businessName.trim(),
      businessDisplayName: input.businessDisplayName.trim() || null,
      phone: input.phone.trim() || null,
      address: input.address.trim() || null,
      branchName: input.branchName.trim(),
      branchCode: input.branchCode.trim() || null,
      branchType: input.branchType,
      stationNumber: Number(input.stationNumber),
      stationName: input.stationName.trim(),
      deviceName: input.deviceName.trim(),
      operatingMode: input.operatingMode || null,
      centralAction: input.centralAction || null,
      centralLocation: input.centralLocation || null,
      centralDatabaseName: input.centralDatabaseName.trim() || null,
      centralServer: input.centralServer.trim() || null,
      centralInstance: input.centralInstance.trim() || null,
      centralPort: input.centralPort.trim() ? Number(input.centralPort) : null,
      centralAuthentication: input.centralAuthentication === "Sql" ? "Sql" : input.centralAuthentication === "Integrated" ? "Integrated" : null,
      centralUsername: input.centralUsername.trim() || null,
      centralPassword: input.centralPassword || null,
      linkToCentral: input.linkToCentral || null,
      centralAdminUserId: input.centralAdminUserId.trim() || null,
      replaceExisting: input.replaceExisting === true,
      branchLocation: input.branchLocation || null,
      branchRemoteChoice: input.branchRemoteChoice || null,
      branchRemoteDatabaseName: input.branchRemoteDatabaseName.trim() || null,
      ...adminPayload(input),
    }),
  });

  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر بدء الإعداد."));
  }

  if (!response.body) {
    throw new SetupClientError("تعذر متابعة الإعداد.");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let finished = false;

  while (!finished) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value, { stream: !done });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";

    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) {
        continue;
      }

      const event = JSON.parse(trimmed) as SetupStreamEvent;
      onEvent(event);
      if (event.type === "completed" || event.type === "failed") {
        finished = true;
      }
    }

    if (done) {
      break;
    }
  }

  if (!finished) {
    throw new SetupClientError("انقطع الاتصال أثناء الإعداد. أعد المحاولة.");
  }
}

export async function getCurrentDevice(session: DeviceSession): Promise<DeviceSession | "revoked" | "offline" | null> {
  let response: Response;
  try {
    response = await fetch("/api/device/current", { headers: deviceHeaders(session) });
  } catch {
    return "offline";
  }

  if (response.status === 401) return null;
  if (response.status === 403) return "revoked";
  if (!response.ok) return "offline";

  const profile = (await response.json()) as Omit<DeviceSession, "deviceToken">;
  return {
    ...session,
    ...profile,
    deviceToken: session.deviceToken,
    deviceType: "POS",
  };
}

export async function sendHeartbeat(session: DeviceSession): Promise<void> {
  await fetch("/api/device/heartbeat", {
    method: "POST",
    headers: deviceHeaders(session),
  });
}

export async function createFirstAdmin(session: DeviceSession, input: AdminSetupInput, authorization = ""): Promise<void> {
  const response = await fetch("/api/setup/create-admin", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Setup-Authorization": authorization,
      ...deviceHeaders(session),
    },
    body: JSON.stringify(adminPayload(input)),
  });

  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر إنشاء حساب مدير النظام."));
  }
}

export async function login(session: DeviceSession, username: string, password: string): Promise<CurrentUser> {
  const response = await fetch("/api/auth/login", {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...deviceHeaders(session),
    },
    body: JSON.stringify({ username: username.trim(), password }),
  });

  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر تسجيل الدخول."));
  }

  return (await response.json()) as CurrentUser;
}

export async function changePassword(
  session: DeviceSession,
  currentPassword: string,
  newPassword: string,
  confirmPassword: string,
): Promise<CurrentUser> {
  const response = await fetch("/api/auth/change-password", {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...deviceHeaders(session),
    },
    body: JSON.stringify({ currentPassword, newPassword, confirmPassword }),
  });

  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر تغيير كلمة المرور."));
  }

  return (await response.json()) as CurrentUser;
}

export async function logout(session: DeviceSession): Promise<void> {
  await fetch("/api/auth/logout", {
    method: "POST",
    credentials: "include",
    headers: deviceHeaders(session),
  });
}

export async function getCurrentUser(session: DeviceSession): Promise<CurrentUser | null> {
  const response = await fetch("/api/auth/me", {
    credentials: "include",
    headers: deviceHeaders(session),
  });

  if (response.status === 401) {
    return null;
  }

  if (!response.ok) {
    throw new SetupClientError("تعذر قراءة بيانات المستخدم.");
  }

  return (await response.json()) as CurrentUser;
}

