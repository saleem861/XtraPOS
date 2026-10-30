import { asDeviceSession, clearDeviceSession, loadDeviceSession } from "./deviceSession";
import type { DeviceSession } from "../types/setup";

const DB_NAME = "xtrapos-device";
const STORE = "config";
const SCHEMA = 1;
const MARKER_KEY = "xtrapos.deviceMarker";
const TAB_KEY = "xtrapos.deviceSession";

export type PersistedDeviceLoad =
  | { kind: "none" }
  | { kind: "ready"; session: DeviceSession }
  | { kind: "unreadable" };

const AGENT_URL = "http://127.0.0.1:47321/device-config";

async function readAgent(): Promise<DeviceSession | null> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), 400);
  try {
    const response = await fetch(AGENT_URL, { headers: { Accept: "application/json" }, signal: controller.signal });
    if (!response.ok) return null;
    return asDeviceSession(await response.json());
  } catch {
    return null;
  } finally {
    window.clearTimeout(timer);
  }
}

export async function loadPersistedDevice(): Promise<PersistedDeviceLoad> {
  void navigator.storage?.persist?.();
  const agent = await readAgent();
  if (agent) {
    await writeIndexed(agent);
    writeMarker(agent);
    return { kind: "ready", session: agent };
  }
  const indexed = await readIndexed();
  if (indexed.kind === "ready") {
    writeMarker(indexed.session);
    return indexed;
  }
  if (indexed.kind === "incompatible") return { kind: "unreadable" };

  const tab = readJson(sessionStorage, TAB_KEY);
  if (tab) {
    await writeIndexed(tab);
    writeMarker(tab);
    clearDeviceSession();
    return { kind: "ready", session: tab };
  }

  const legacy = loadDeviceSession();
  if (legacy) {
    await writeIndexed(legacy);
    writeMarker(legacy);
    sessionStorage.setItem(TAB_KEY, JSON.stringify(legacy));
    clearDeviceSession();
    return { kind: "ready", session: legacy };
  }

  return markerPresent() ? { kind: "unreadable" } : { kind: "none" };
}

export async function savePersistedDevice(session: DeviceSession): Promise<void> {
  try {
    await fetch(AGENT_URL, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(session) });
  } catch {
    // The local agent is optional. Browser storage remains the fallback.
  }
  await writeIndexed(session);
  writeMarker(session);
  sessionStorage.setItem(TAB_KEY, JSON.stringify(session));
  clearDeviceSession();
}

export async function clearPersistedDevice(): Promise<void> {
  clearDeviceSession();
  sessionStorage.removeItem(TAB_KEY);
  localStorage.removeItem(MARKER_KEY);
  const db = await openDb();
  if (!db) return;
  await new Promise<void>((resolve) => {
    const tx = db.transaction(STORE, "readwrite");
    tx.objectStore(STORE).delete("current");
    tx.oncomplete = () => resolve();
    tx.onerror = () => resolve();
  });
}

function writeMarker(session: DeviceSession): void {
  localStorage.setItem(MARKER_KEY, JSON.stringify({
    schemaVersion: SCHEMA,
    deviceId: session.deviceId,
    businessId: session.businessId,
    branchId: session.branchId,
    stationId: session.stationId,
    deviceName: session.deviceName,
    origin: location.origin,
  }));
}

function markerPresent(): boolean {
  return localStorage.getItem(MARKER_KEY) !== null;
}

function readJson(storage: Storage, key: string): DeviceSession | null {
  try {
    const raw = storage.getItem(key);
    return raw ? asDeviceSession(JSON.parse(raw)) : null;
  } catch {
    return null;
  }
}

async function readIndexed(): Promise<{ kind: "empty" | "incompatible" } | { kind: "ready"; session: DeviceSession }> {
  const db = await openDb();
  if (!db) return { kind: "empty" };
  return new Promise((resolve) => {
    const tx = db.transaction(STORE, "readonly");
    const request = tx.objectStore(STORE).get("current");
    request.onsuccess = () => {
      const value = request.result as { schemaVersion?: number } | undefined;
      if (value && typeof value.schemaVersion === "number" && value.schemaVersion > SCHEMA) {
        resolve({ kind: "incompatible" });
        return;
      }
      const session = asDeviceSession(value);
      resolve(session ? { kind: "ready", session } : { kind: "empty" });
    };
    request.onerror = () => resolve({ kind: "empty" });
  });
}

async function writeIndexed(session: DeviceSession): Promise<void> {
  const db = await openDb();
  if (!db) return;
  await new Promise<void>((resolve) => {
    const tx = db.transaction(STORE, "readwrite");
    tx.objectStore(STORE).put({ ...session, schemaVersion: SCHEMA, lastValidatedAt: new Date().toISOString() }, "current");
    tx.oncomplete = () => resolve();
    tx.onerror = () => resolve();
  });
}

function openDb(): Promise<IDBDatabase | null> {
  if (typeof indexedDB === "undefined") return Promise.resolve(null);
  return new Promise((resolve) => {
    const request = indexedDB.open(DB_NAME, SCHEMA);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE)) request.result.createObjectStore(STORE);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => resolve(null);
  });
}
