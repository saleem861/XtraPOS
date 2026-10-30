import { DEVICE_TYPE_POS, isBranchType, type DeviceSession } from "../types/setup";

export const DEVICE_SESSION_STORAGE_KEY = "xtrapos.deviceSession";
const LEGACY_REGISTRATION_KEY = "xtrapos.deviceRegistration";

function isGuid(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

export function asDeviceSession(value: unknown): DeviceSession | null {
  return isSession(value) ? value : null;
}

function isSession(value: unknown): value is DeviceSession {
  if (!value || typeof value !== "object") {
    return false;
  }

  const record = value as Record<string, unknown>;
  return (
    isGuid(record.deviceId) &&
    isGuid(record.businessId) &&
    isGuid(record.branchId) &&
    isGuid(record.stationId) &&
    typeof record.deviceName === "string" &&
    record.deviceName.trim().length > 0 &&
    record.deviceType === DEVICE_TYPE_POS &&
    typeof record.deviceToken === "string" &&
    record.deviceToken.length > 0 &&
    typeof record.registeredAt === "string" &&
    record.registeredAt.length > 0 &&
    typeof record.businessName === "string" &&
    record.businessName.trim().length > 0 &&
    typeof record.branchName === "string" &&
    record.branchName.trim().length > 0 &&
    isBranchType(record.branchType) &&
    typeof record.stationName === "string" &&
    record.stationName.trim().length > 0 &&
    typeof record.stationNumber === "number" &&
    record.stationNumber > 0
  );
}

export function loadDeviceSession(): DeviceSession | null {
  try {
    localStorage.removeItem(LEGACY_REGISTRATION_KEY);
    const raw = localStorage.getItem(DEVICE_SESSION_STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed: unknown = JSON.parse(raw);
    return asDeviceSession(parsed);
  } catch {
    return null;
  }
}

export function saveDeviceSession(session: DeviceSession): void {
  localStorage.setItem(DEVICE_SESSION_STORAGE_KEY, JSON.stringify(session));
}

export function clearDeviceSession(): void {
  localStorage.removeItem(DEVICE_SESSION_STORAGE_KEY);
  localStorage.removeItem(LEGACY_REGISTRATION_KEY);
}
