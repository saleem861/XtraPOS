import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export const CUSTOMER_DISPLAY_ENABLED = "customerDisplay.enabled";
export const ALL_POINTS = "all";

export type DeviceSettingSource = "Branch" | "Central";

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

/** نقطة بيع في أي فرع مع قيمها الأولية كما هي في جدول هذا الفرع (نسخة من جدول المركز). */
export interface PointSettings {
  deviceId: string;
  branchId: string;
  branchName: string | null;
  posNumber: number | null;
  deviceName: string | null;
  isCurrent: boolean;
  settings: DeviceSettingValue[];
  centralVersion: number;
  syncedAtUtc: string | null;
  updatedSource: DeviceSettingSource | null;
}

export interface PointsList {
  points: PointSettings[];
  canManageAll: boolean;
  notice: string | null;
}

/** عملية من سجل الترحيل الموجود (BranchOperationLog) نوعها DeviceSettings. */
export interface DeviceSettingsLogRow {
  operationId: string;
  referenceNumber: string | null;
  description: string;
  status: "Pending" | "Executing" | "Completed" | "Blocked" | "Cancelled";
  resultMessage: string | null;
  attemptCount: number;
  createdAt: string;
  completedAt: string | null;
  actorName: string | null;
  allPoints: boolean;
}

export interface SaveResult {
  operationId: string;
  points: number;
  message: string;
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

export function settingValue(settings: DeviceSettingValue[] | undefined, key: string): boolean {
  return settings?.find((setting) => setting.key === key)?.value ?? false;
}

export function pointLabel(point: Pick<PointSettings, "branchName" | "deviceName" | "posNumber">) {
  const name = point.deviceName?.trim() || "نقطة";
  const number = point.posNumber != null ? ` (${point.posNumber})` : "";
  return point.branchName?.trim() ? `${point.branchName.trim()} · ${name}${number}` : `${name}${number}`;
}

export function createDeviceSettingsClient(session: DeviceSession) {
  const headers = deviceHeaders(session);
  const jsonHeaders = { ...headers, "Content-Type": "application/json" };

  return {
    async current() {
      const response = await fetch("/api/device-settings/current", { headers });
      return readJson<PointSettings>(response, "تعذر قراءة القيم الأولية لهذه النقطة.");
    },
    async points() {
      const response = await fetch("/api/device-settings/points", { headers });
      return readJson<PointsList>(response, "تعذر تحميل نقاط البيع.");
    },
    async history(deviceId: string) {
      const response = await fetch(`/api/device-settings/history?deviceId=${encodeURIComponent(deviceId)}`, { headers });
      return readJson<DeviceSettingsLogRow[]>(response, "تعذر قراءة سجل الترحيل.");
    },
    async save(target: PointSettings | typeof ALL_POINTS, values: DeviceSettingChanges) {
      const body = target === ALL_POINTS
        ? { scope: "All", values }
        : {
            scope: "Device",
            deviceId: target.deviceId,
            point: {
              deviceId: target.deviceId,
              branchId: target.branchId,
              branchName: target.branchName,
              posNumber: target.posNumber,
              deviceName: target.deviceName,
            },
            values,
          };
      const response = await fetch("/api/device-settings", { method: "PUT", headers: jsonHeaders, body: JSON.stringify(body) });
      return readJson<SaveResult>(response, "تعذر حفظ القيم الأولية.");
    },
  };
}

export type DeviceSettingsClient = ReturnType<typeof createDeviceSettingsClient>;
