import { normalizeCartDraft, scopeKey, type CartDraft, type CartScope } from "./model";

export interface DraftDatabase {
  get(key: string, run?: string): Promise<CartDraft | null>;
  put(draft: CartDraft): Promise<void>;
  remove(key: string): Promise<void>;
}

export interface CartRestoreView {
  effectRunId: number;
  cartRestore: "loading" | "ready" | "unavailable";
  cart: "null" | "present";
  scopeKey: "present" | "missing";
}

export function createMemoryDraftDatabase(): DraftDatabase {
  const rows = new Map<string, CartDraft>();
  return {
    async get(key) {
      const row = rows.get(key);
      return row ? structuredClone(row) : null;
    },
    async put(draft) {
      rows.set(scopeKey(draft.scope), structuredClone(draft));
    },
    async remove(key) {
      rows.delete(key);
    },
  };
}

const DB_NAME = "xtrapos-pos";
const STORE = "cartDrafts";

function requestToPromise<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

const DB_VERSION = 1;

const storageLines: string[] = [];
const storageListeners = new Set<(lines: string[]) => void>();
let restoreView: CartRestoreView = {
  effectRunId: 0,
  cartRestore: "loading",
  cart: "null",
  scopeKey: "missing",
};
const viewListeners = new Set<(view: CartRestoreView) => void>();

export function publishCartRestoreView(next: CartRestoreView) {
  if (!import.meta.env.DEV) return;
  restoreView = next;
  for (const listener of viewListeners) listener(restoreView);
}

export function subscribeCartRestoreView(listener: (view: CartRestoreView) => void) {
  listener(restoreView);
  viewListeners.add(listener);
  return () => viewListeners.delete(listener);
}

export function subscribeCartStorageLog(listener: (lines: string[]) => void) {
  listener([...storageLines]);
  storageListeners.add(listener);
  return () => storageListeners.delete(listener);
}

export function clearCartStorageLog() {
  storageLines.length = 0;
  for (const listener of storageListeners) listener([]);
}

function traceStorage(event: string) {
  if (!import.meta.env.DEV) return;
  const stamp = new Date().toLocaleTimeString("en-GB", { hour12: false });
  const line = `${stamp} ${event}`;
  console.info("[xtrapos-pos]", line);
  storageLines.push(line);
  if (storageLines.length > 40) storageLines.shift();
  const snapshot = [...storageLines];
  for (const listener of storageListeners) listener(snapshot);
}

export function recordCartStorageEvent(event: string) {
  traceStorage(event);
}

export function openCartDatabase(run = ""): Promise<IDBDatabase> {
  const note = (event: string) => traceStorage(run ? `${run} ${event}` : event);
  note("restore-start");
  note(typeof indexedDB === "undefined" ? "indexeddb-missing" : "indexeddb-available");
  return new Promise((resolve, reject) => {
    let settled = false;
    const open = indexedDB.open(DB_NAME, DB_VERSION);
    note("db-open-requested");
    note("open-requested");
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      note("timeout");
      reject(new Error("انتهت مهلة قراءة السلة المحلية."));
    }, 3000);
    open.onupgradeneeded = () => {
      note("onupgradeneeded");
      if (!open.result.objectStoreNames.contains(STORE)) {
        open.result.createObjectStore(STORE, { keyPath: "scopeKey" });
      }
    };
    open.onblocked = () => {
      note("onblocked");
    };
    open.onerror = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      note("onerror");
      reject(open.error ?? new Error("تعذر فتح السلة المحلية."));
    };
    open.onsuccess = () => {
      const database = open.result;
      if (settled) {
        note("late-success-closed");
        database.close();
        return;
      }
      settled = true;
      clearTimeout(timer);
      database.onversionchange = () => {
        note("versionchange");
        database.close();
      };
      note("onsuccess");
      note("db-open-success");
      resolve(database);
    };
  });
}

export function createIndexedDbDraftDatabase(openDatabase: (run?: string) => Promise<IDBDatabase> = openCartDatabase): DraftDatabase {
  return {
    async get(key, run = "") {
      const note = (event: string) => traceStorage(run ? `${run} ${event}` : event);
      const database = await openDatabase(run);
      note("draft-transaction-start");
      const transaction = database.transaction(STORE, "readonly");
      transaction.oncomplete = () => note("draft-transaction-complete");
      transaction.onerror = () => note("draft-transaction-error");
      transaction.onabort = () => note("draft-transaction-abort");
      const request = transaction.objectStore(STORE).get(key);
      note("draft-get-requested");
      try {
        const row = await requestToPromise(request);
        note(row ? "draft-get-success" : "draft-get-empty");
        database.close();
        return row ? (row as { draft: CartDraft }).draft : null;
      } catch (error) {
        note("draft-get-error");
        database.close();
        throw error;
      }
    },
    async put(draft) {
      const database = await openDatabase();
      await requestToPromise(database.transaction(STORE, "readwrite").objectStore(STORE).put({ scopeKey: scopeKey(draft.scope), draft }));
      database.close();
    },
    async remove(key) {
      const database = await openDatabase();
      await requestToPromise(database.transaction(STORE, "readwrite").objectStore(STORE).delete(key));
      database.close();
    },
  };
}

export async function loadDraft(database: DraftDatabase, scope: CartScope, run = ""): Promise<CartDraft | null> {
  const note = (event: string) => traceStorage(run ? `${run} ${event}` : event);
  try {
    const draft = await database.get(scopeKey(scope), run);
    if (!draft || scopeKey(draft.scope) !== scopeKey(scope)) {
      note("loadDraft-resolved-null");
      return null;
    }
    note("loadDraft-resolved-draft");
    return normalizeCartDraft(draft);
  } catch (error) {
    note("loadDraft-rejected");
    throw error;
  }
}

export async function saveDraft(database: DraftDatabase, draft: CartDraft): Promise<void> {
  await database.put(draft);
}
