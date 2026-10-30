import { useEffect, useState } from "react";
import { clearCartStorageLog, subscribeCartRestoreView, subscribeCartStorageLog, type CartRestoreView } from "../cart/draftStore";

export function idbDebugRequested(search = window.location.search): boolean {
  return import.meta.env.DEV && new URLSearchParams(search).get("debug") === "idb";
}

export function IdbDebugPanel() {
  const [lines, setLines] = useState<string[]>([]);
  const [view, setView] = useState<CartRestoreView>({ effectRunId: 0, cartRestore: "loading", cart: "null", scopeKey: "missing" });
  const [open, setOpen] = useState(true);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    subscribeCartStorageLog(setLines);
  }, []);
  useEffect(() => {
    subscribeCartRestoreView(setView);
  }, []);

  if (!idbDebugRequested()) return null;

  async function copyLog() {
    const text = lines.join("\n");
    if (navigator.clipboard) await navigator.clipboard.writeText(text);
    setCopied(true);
  }

  return (
    <aside className="idb-debug" aria-label="سجل التخزين المحلي">
      <button type="button" onClick={() => setOpen((value) => !value)}>{open ? "طي السجل" : "سجل التخزين"}</button>
      {open ? (
        <>
          <p>Effect run: {view.effectRunId}</p>
          <p>cartRestore: {view.cartRestore}</p>
          <p>cart: {view.cart}</p>
          <p>scopeKey: {view.scopeKey}</p>
          <p>DB: xtrapos-pos</p>
          <p>Requested version: 1</p>
          <p>Stores after success: cartDrafts</p>
          <pre>{lines.join("\n")}</pre>
          <button type="button" onClick={() => void copyLog()}>{copied ? "تم النسخ" : "نسخ السجل"}</button>
          <button type="button" onClick={() => { clearCartStorageLog(); setCopied(false); }}>مسح</button>
        </>
      ) : null}
    </aside>
  );
}
