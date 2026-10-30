import { SetupClientError } from "../api/client";
import { asDeviceSession } from "../storage/deviceSession";
import type { BranchSetupInput, DeviceSession } from "../types/setup";

export interface SecondaryStation {
  stationId: string;
  stationNumber: number;
  stationName: string;
}

export function secondaryApiUrl(draft: BranchSetupInput, path: string): string {
  const host = draft.secondaryHost.trim() || "localhost";
  const port = draft.secondaryPort.trim() || "5080";
  return `http://${host}:${port}${path}`;
}

export function secondaryRegistrationBody(draft: BranchSetupInput) {
  return {
    branchId: draft.selectedBranchId,
    stationId: draft.stationId,
    deviceName: draft.deviceName.trim(),
    pairingToken: draft.pairingToken,
  };
}

export async function verifySecondaryPairing(draft: BranchSetupInput): Promise<string> {
  const response = await fetch(secondaryApiUrl(draft, "/api/devices/pairing-codes/verify"), {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({
      branchId: draft.selectedBranchId,
      code: draft.pairingCode.trim(),
    }),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "رمز الربط غير صالح أو انتهت صلاحيته."));
  }

  const body = (await response.json()) as { pairingToken?: string };
  if (!body.pairingToken?.trim()) {
    throw new SetupClientError("رمز الربط غير صالح أو انتهت صلاحيته.");
  }

  return body.pairingToken;
}

export async function listExistingBranchStations(draft: BranchSetupInput): Promise<SecondaryStation[]> {
  const response = await fetch("/api/setup/branch-stations", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({
      databaseName: draft.branchDatabaseName.trim(),
      server: draft.branchServer.trim() || "localhost",
      instanceName: draft.branchInstance.trim() || null,
      port: draft.branchPort.trim() ? Number(draft.branchPort) : null,
      authenticationType: draft.branchAuthentication || "Integrated",
      username: draft.branchUsername.trim() || null,
      password: draft.branchPassword || null,
      expectedRole: "Branch",
    }),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر قراءة نقاط البيع."));
  }

  const body = (await response.json()) as { stations?: SecondaryStation[] };
  if (!Array.isArray(body.stations)) {
    throw new SetupClientError("تعذر قراءة نقاط البيع.");
  }

  return body.stations;
}

export async function listSecondaryStations(draft: BranchSetupInput): Promise<SecondaryStation[]> {
  const url = `${secondaryApiUrl(draft, "/api/devices/stations")}?branchId=${encodeURIComponent(draft.selectedBranchId)}`;
  const response = await fetch(url, {
    headers: {
      Accept: "application/json",
      "X-Pairing-Token": draft.pairingToken,
    },
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر قراءة نقاط البيع."));
  }

  const body = (await response.json()) as SecondaryStation[];
  if (!Array.isArray(body)) {
    throw new SetupClientError("تعذر قراءة نقاط البيع.");
  }

  return body;
}

export async function createSecondaryStation(draft: BranchSetupInput): Promise<SecondaryStation> {
  const response = await fetch(secondaryApiUrl(draft, "/api/devices/stations"), {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({
      branchId: draft.selectedBranchId,
      stationNumber: Number(draft.stationNumber),
      stationName: draft.stationName.trim(),
      pairingToken: draft.pairingToken,
    }),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر إنشاء نقطة البيع."));
  }

  return (await response.json()) as SecondaryStation;
}

export async function registerSecondaryDevice(draft: BranchSetupInput): Promise<DeviceSession> {
  if (!draft.pairingVerified || !draft.pairingToken.trim()) {
    throw new SetupClientError("يجب التحقق من رمز ربط الجهاز قبل تسجيله.");
  }

  const body = secondaryRegistrationBody(draft);
  const response = await fetch(secondaryApiUrl(draft, "/api/devices/register"), {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new SetupClientError(await readMessage(response, "تعذر ربط هذا الجهاز."));
  }

  const session = asDeviceSession(await response.json());
  if (!session) {
    throw new SetupClientError("تعذر قراءة نتيجة ربط الجهاز.");
  }

  return session;
}

async function readMessage(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { message?: string };
    return body.message?.trim() || fallback;
  } catch {
    return fallback;
  }
}
