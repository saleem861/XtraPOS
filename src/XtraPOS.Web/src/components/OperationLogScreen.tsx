import { useCallback, useEffect, useState } from "react";
import { deviceHeaders } from "../api/client";
import type { DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

interface OperationLogRow {
  serial: number;
  referenceNumber: string;
  operationId: string;
  operationType: string;
  description: string;
  payload: string;
  destinationName: string;
  status: string;
  resultMessage: string;
  attemptCount: number;
  createdAt: string;
}

interface OperationLogPage {
  headSerial: number | null;
  rows: OperationLogRow[];
  destinations: string[];
}

const statusText: Record<string, string> = {
  Pending: "بانتظار التنفيذ",
  Executing: "قيد التنفيذ",
  Completed: "تم التنفيذ",
  Blocked: "متوقف",
  Cancelled: "ملغى",
};

export function OperationLogScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const [page, setPage] = useState<OperationLogPage>({ headSerial: null, rows: [], destinations: [] });
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [destination, setDestination] = useState("");
  const [live, setLive] = useState(false);
  const [error, setError] = useState("");
  const [pendingId, setPendingId] = useState("");
  const [confirmDelete, setConfirmDelete] = useState<OperationLogRow | null>(null);

  const load = useCallback(async () => {
    const query = new URLSearchParams();
    if (from) query.set("from", from);
    if (to) query.set("to", to);
    if (destination) query.set("destination", destination);
    const response = await fetch(`/api/operations/log?${query.toString()}`, { credentials: "include", headers: deviceHeaders(session) });
    if (!response.ok) {
      setError("تعذر قراءة سجل الترحيل.");
      return;
    }

    setError("");
    setPage(await response.json() as OperationLogPage);
  }, [session, from, to, destination]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!live) {
      return;
    }

    const timer = window.setInterval(() => void load(), 4000);
    return () => window.clearInterval(timer);
  }, [live, load]);

  async function retry(row: OperationLogRow) {
    setPendingId(row.operationId);
    const response = await fetch(`/api/operations/log/${row.operationId}/retry`, { method: "POST", credentials: "include", headers: deviceHeaders(session) });
    setPendingId("");
    if (!response.ok) {
      setError("لا يمكن إعادة محاولة هذا الأمر.");
      return;
    }

    await load();
  }

  async function remove(row: OperationLogRow) {
    setPendingId(row.operationId);
    const response = await fetch(`/api/operations/log/${row.operationId}`, { method: "DELETE", credentials: "include", headers: deviceHeaders(session) });
    setPendingId("");
    setConfirmDelete(null);
    if (!response.ok) {
      setError("تعذر حذف الأمر.");
      return;
    }

    await load();
  }

  return (
    <section className="card operation-log-screen">
      <ScreenHeader title="سجل الترحيل" onBack={onBack} />
      <div className="operation-log-tools">
        <label>من<input type="date" value={from} onChange={(event) => setFrom(event.target.value)} /></label>
        <label>إلى<input type="date" value={to} onChange={(event) => setTo(event.target.value)} /></label>
        <label>الوجهة
          <select value={destination} onChange={(event) => setDestination(event.target.value)}>
            <option value="">الكل</option>
            {page.destinations.map((name) => <option key={name} value={name}>{name}</option>)}
          </select>
        </label>
        <button className="ghost-button" type="button" onClick={() => void load()}>تحديث</button>
        <button className={live ? "primary-button" : "ghost-button"} type="button" aria-pressed={live} onClick={() => setLive((value) => !value)}>
          {live ? "المباشر يعمل" : "مباشر"}
        </button>
      </div>
      {error ? <p className="form-error">{error}</p> : null}
      {confirmDelete ? (
        <div className="operation-log-warning" role="alertdialog" aria-label="تأكيد حذف أمر الترحيل">
          <p>حذف أمر «{confirmDelete.description}» يوقفه عن الترحيل. الفاتورة وقيد الدين المحلي يبقيان.</p>
          <button className="primary-button" type="button" disabled={pendingId === confirmDelete.operationId} onClick={() => void remove(confirmDelete)}>تأكيد الحذف</button>
          <button className="ghost-button" type="button" onClick={() => setConfirmDelete(null)}>رجوع</button>
        </div>
      ) : null}
      <div className="operation-log-table-wrap">
        <table className="data-table operation-log-table">
          <thead>
            <tr>
              <th>المرجع</th>
              <th>الوقت</th>
              <th>النوع</th>
              <th>الوصف</th>
              <th>الأمر</th>
              <th>الوجهة</th>
              <th>الحالة</th>
              <th>النتيجة</th>
              <th>إجراءات</th>
            </tr>
          </thead>
          <tbody>
            {page.rows.length === 0 ? (
              <tr><td colSpan={9}>لا توجد أوامر ترحيل.</td></tr>
            ) : page.rows.map((row) => (
              <tr key={row.operationId} className={row.serial === page.headSerial ? "operation-log-stuck" : undefined}>
                <td>{row.referenceNumber || row.serial}</td>
                <td>{new Date(row.createdAt).toLocaleString("ar")}</td>
                <td>{row.operationType}</td>
                <td>{row.description}</td>
                <td className="operation-log-command">{row.payload}</td>
                <td>{row.destinationName}</td>
                <td>{statusText[row.status] ?? row.status}</td>
                <td>{row.resultMessage}</td>
                <td className="operation-log-actions">
                  <button className="ghost-button" type="button" disabled={pendingId === row.operationId || row.status === "Completed" || row.status === "Cancelled"} onClick={() => void retry(row)}>إعادة محاولة</button>
                  <button className="ghost-button" type="button" disabled={pendingId === row.operationId} onClick={() => setConfirmDelete(row)}>حذف</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
