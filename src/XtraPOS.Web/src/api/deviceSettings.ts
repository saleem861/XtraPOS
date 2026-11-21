import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export const CUSTOMER_DISPLAY_ENABLED = "customerDisplay.enabled";

export type DeviceSettingSource = "Branch" | "Central";
export type DeviceSettingsSyncState = "LocalOnly" | "PendingCentral" | "ReconciliationRequired" | "Synced";

export interface DeviceSettingValue {
  key: string;
  label: string;
  kind: "boolean";
  value: boolean;
  isDefault: boolean;
  updatedAtUtc: string | null;
  updatedByName: string | null;
  source: DeviceSettingSource | null;
}

export interface CurrentDeviceSettings {
  deviceId: string;
  branchId: string;
  branchName: string | null;
  posNumber: number | null;
  deviceName: string | null;
  settings: DeviceSettingValue[];
  syncState: DeviceSettingsSyncState;
  pendingTransfers: number;
  lastError: string | null;
  diagnosticId: string | null;
  centralVersion: number | null;
  syncedAtUtc: string | null;
}

export type TransferStatus = "Pending" | "Completed" | "ReconciliationRequired";

/** سطر من سجل الترحيلات: حفظ واحد من الفرع ومصير ترحيله إلى المركز. */
export interface DeviceSettingsTransfer {
  outboxEventId: string;
  changedKeys: string[];
  createdByName: string | null;
  status: TransferStatus;
  attemptCount: number;
  createdAt: string;
  lastAttemptAt: string | null;
  nextRetryAt: string | null;
  completedAt: string | null;
  centralVersion: number | null;
  lastError: string | null;
  diagnosticId: string | null;
}

export interface DeviceSettingsReceived {
  changeSequence: number;
  centralVersion: number;
  outcome: "Applied" | "NoChange";
  appliedAt: string;
}

export interface DeviceSettingsHistory {
  transfers: DeviceSettingsTransfer[];
  received: DeviceSettingsReceived[];
}

export interface DeviceSettingsAuditEntry {
  settingKey: string;
  label: string | null;
  beforeValue: string | null;
  afterValue: string | null;
  source: DeviceSettingSource;
  actorName: string | null;
  outboxEventId: string | null;
  settingsVersion: number;
  occurredAt: string;
}

export interface CentralDeviceSettings {
  deviceId: string;
  branchId: string;
  branchName: string | null;
  posNumber: number | null;
  deviceName: string | null;
  settings: DeviceSettingValue[];
  settingsVersion: number;
  updatedAtUtc: string | null;
  updatedSource: DeviceSettingSource | null;
}

export type DeviceSettingChanges = Record<string, boolean>;

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) {
    throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  }

  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { message?: string };
      if (body.message?.trim()) {
        message = body.message;
      }
    } catch {
      message = fallback;
    }
    throw new SetupClientError(message);
  }

  return (await response.json()) as T;
}

export function settingValue(settings: DeviceSettingValue[], key: string): boolean {
  return settings.find((setting) => setting.key === key)?.value ?? false;
}

export function createDeviceSettingsClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  const jsonHeaders = { ...headers, "Content-Type": "application/json" };

  return {
    async current() {
      const response = await fetch("/api/device-settings/current", { headers });
      return readJson<CurrentDeviceSettings>(response, "تعذر قراءة القيم الأولية لهذه النقطة.");
    },
    async saveCurrent(values: DeviceSettingChanges) {
      const response = await fetch("/api/device-settings/current", {
        method: "PUT",
        headers: jsonHeaders,
        body: JSON.stringify({ values }),
      });
      return readJson<CurrentDeviceSettings>(response, "تعذر حفظ القيم الأولية.");
    },
    async history() {
      const response = await fetch("/api/device-settings/current/history", { headers });
      return readJson<DeviceSettingsHistory>(response, "تعذر قراءة سجل الترحيلات.");
    },
    async centralHistory(deviceId: string) {
      const response = await fetch(`/api/device-settings/central/${encodeURIComponent(deviceId)}/history`, { headers });
      return readJson<DeviceSettingsAuditEntry[]>(response, "تعذر قراءة سجل تعديلات النقطة.");
    },
    async listCentral() {
      const response = await fetch("/api/device-settings/central", { headers });
      return readJson<CentralDeviceSettings[]>(response, "تعذر تحميل نقاط البيع من الإدارة المركزية.");
    },
    async saveCentral(deviceId: string, values: DeviceSettingChanges) {
      const response = await fetch(`/api/device-settings/central/${encodeURIComponent(deviceId)}`, {
        method: "PUT",
        headers: jsonHeaders,
        body: JSON.stringify({ values }),
      });
      return readJson<CentralDeviceSettings>(response, "تعذر حفظ إعدادات النقطة في الإدارة المركزية.");
    },
  };
}

export type DeviceSettingsClient = ReturnType<typeof createDeviceSettingsClient>;
