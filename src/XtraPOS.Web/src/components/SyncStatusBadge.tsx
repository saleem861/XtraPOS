import { useEffect, useState } from "react";

type SyncState = "InProgress" | "Success" | "Error" | "Offline" | "Unlinked" | "Connected";

interface SyncPayload {
  state?: string;
  operation?: string | null;
  detail?: string | null;
  diagnosticId?: string | null;
  updatedAtUtc?: string;
}

const labels: Record<SyncState, string> = {
  InProgress: "جاري المزامنة",
  Success: "تمت المزامنة",
  Error: "خطأ في المزامنة",
  Offline: "الاتصال غير متاح",
  Unlinked: "غير مرتبط بالمركز",
  Connected: "متصل بالمركز",
};

export function SyncStatusBadge() {
  const [open, setOpen] = useState(false);
  const [payload, setPayload] = useState<SyncPayload>({ state: "Offline" });

  useEffect(() => {
    let stopped = false;
    async function load() {
      try {
        const response = await fetch("/api/sync/status");
        if (!response.ok) {
          if (!stopped) setPayload({ state: "Offline", detail: "تعذر قراءة حالة المزامنة." });
          return;
        }
        const body = await response.json() as SyncPayload;
        if (!stopped) setPayload(body);
      } catch {
        if (!stopped) setPayload({ state: "Offline", detail: "الخادم غير متاح. الإعدادات المحلية تبقى محفوظة." });
      }
    }
    void load();
    const timer = window.setInterval(() => void load(), 15000);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, []);

  const state = (payload.state === "InProgress" || payload.state === "Success" || payload.state === "Error" || payload.state === "Offline" || payload.state === "Unlinked" || payload.state === "Connected")
    ? payload.state
    : "Offline";

  return (
    <div className="sync-badge">
      <button type="button" className={`sync-badge-button sync-${state.toLowerCase()}`} onClick={() => setOpen((value) => !value)}>
        {labels[state]}
      </button>
      {open ? (
        <div className="sync-badge-detail">
          <p>{payload.detail || labels[state]}</p>
          {payload.operation ? <p>العملية: {payload.operation}</p> : null}
          {payload.updatedAtUtc ? <p>آخر تحديث: {payload.updatedAtUtc}</p> : null}
          {payload.diagnosticId ? <p>رمز التشخيص: {payload.diagnosticId}</p> : null}
        </div>
      ) : null}
    </div>
  );
}
