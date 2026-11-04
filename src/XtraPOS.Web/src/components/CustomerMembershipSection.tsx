import { useEffect, useState } from "react";
import QRCode from "qrcode";
import { deviceHeaders } from "../api/client";
import { traceUi } from "../diagnostics/trace";
import type { DeviceSession } from "../types/setup";

interface Summary {
  configured?: boolean;
  activeSessions?: number;
  providers?: string[];
  lastVerifiedAtUtc?: string | null;
}

export function CustomerMembershipSection({ session, customerId, canUnlink, canExcept = false, canApproveException = false, pendingEnrollment = false, onBeforeEnroll, onException }: {
  session: DeviceSession;
  customerId: string;
  canUnlink: boolean;
  canExcept?: boolean;
  canApproveException?: boolean;
  pendingEnrollment?: boolean;
  onBeforeEnroll?: () => void;
  onException?: (enabled: boolean) => void;
}) {
  const [summary, setSummary] = useState<Summary | null>(null);
  const [open, setOpen] = useState(false);
  const [notice, setNotice] = useState("");
  const [exceptionPending, setExceptionPending] = useState(false);
  const [withoutMembership, setWithoutMembership] = useState(false);
  const [qrImage, setQrImage] = useState("");
  const [qrError, setQrError] = useState("");

  async function startQr() {
    onBeforeEnroll?.();
    setQrError("");
    setQrImage("");
    const response = await fetch("/api/membership/enrollment-qr", {
      method: "POST",
      credentials: "same-origin",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({ customerId: customerId || null }),
    });
    const body = await response.json().catch(() => ({})) as { token?: string; message?: string };
    if (!response.ok || !body.token) {
      const message = body.message || "تعذر توليد رمز العضوية.";
      setQrError(message);
      traceUi("customer", "enrollment-qr", message);
      return;
    }
    traceUi("customer", "enrollment-qr", "created");
    const url = `${window.location.origin}/membership/enroll/${body.token}`;
    setQrImage(await QRCode.toDataURL(url, { margin: 1, width: 220 }));
  }

  useEffect(() => {
    if (!customerId) return;
    let stop = false;
    void fetch(`/api/customers/${customerId}/membership`, { credentials: "same-origin", headers: deviceHeaders(session) }).then(async (response) => {
      if (stop) return;
      if (!response.ok) {
        setNotice("تعذر قراءة حالة العضوية.");
        return;
      }
      setNotice("");
      setSummary(await response.json() as Summary);
    }).catch(() => { if (!stop) setNotice("تعذر قراءة حالة العضوية."); });
    return () => { stop = true; };
  }, [customerId]);

  async function revoke() {
    const response = await fetch(`/api/customers/${customerId}/membership/revoke-all`, { method: "POST", credentials: "same-origin", headers: deviceHeaders(session) });
    setNotice(response.ok ? "تم إلغاء الجلسات النشطة." : "تعذر إلغاء الجلسات.");
    if (response.ok) {
      const next = await fetch(`/api/customers/${customerId}/membership`, { credentials: "same-origin", headers: deviceHeaders(session) });
      if (next.ok) setSummary(await next.json() as Summary);
    }
  }

  async function unlink() {
    const response = await fetch(`/api/customers/${customerId}/membership/unlink`, { method: "POST", credentials: "same-origin", headers: deviceHeaders(session) });
    setNotice(response.ok ? "تم إلغاء ربط العضوية." : "تعذر إلغاء الربط.");
    if (response.ok) setSummary({ configured: summary?.configured, activeSessions: 0, providers: [] });
  }

  useEffect(() => {
    if (!customerId) return;
    void fetch(`/api/customers/${customerId}/membership/exception`, { credentials: "same-origin", headers: deviceHeaders(session) }).then(async (response) => {
      if (!response.ok) return;
      const body = await response.json() as { pending?: boolean };
      setExceptionPending(Boolean(body.pending));
    }).catch(() => undefined);
  }, [customerId]);

  if (!customerId) {
    return (
      <section className="membership-compact" aria-label="العضوية الذكية">
        <strong>العضوية الذكية</strong>
        <span>{pendingEnrollment ? "بانتظار الحفظ" : "غير مرتبطة"}</span>
        {pendingEnrollment ? <p>تم التحقق. اضغط حفظ لإنشاء الزبون وربط العضوية.</p> : <p>يمكن تسجيل العضوية قبل حفظ الزبون.</p>}
        {!pendingEnrollment ? <button type="button" onClick={() => void startQr()}>تسجيل العضوية</button> : null}
        {qrImage ? <img src={qrImage} alt="رمز تسجيل العضوية" width={220} height={220} /> : null}
        {qrError ? <p role="status">{qrError}</p> : null}
        {canExcept ? <label><input type="checkbox" checked={withoutMembership} onChange={(event) => { const next = event.target.checked; if (next && !window.confirm("سيتم تسجيل الزبون بدون تعريف إلكتروني. هل تؤكد هذا الاستثناء؟")) return; setWithoutMembership(next); onException?.(next); }} /> تسجيل الزبون بدون تعريف إلكتروني</label> : null}
      </section>
    );
  }

  const providers = summary?.providers ?? [];
  const linked = providers.length > 0;

  return (
    <section className={`membership-compact${linked ? " is-linked" : ""}`} aria-label="العضوية الذكية">
      <strong>العضوية الذكية</strong>
      <span>{linked ? "مرتبطة" : "غير مرتبطة"}</span>
      {linked ? providers.map((provider) => <em key={provider}>{provider}</em>) : null}
      <button type="button" onClick={() => void startQr()}>{linked ? "تعديل العضوية الذكية" : "تسجيل العضوية"}</button>
      {qrImage ? <img src={qrImage} alt="رمز تسجيل العضوية" width={220} height={220} /> : null}
      {qrError ? <p role="status">{qrError}</p> : null}
      {linked ? <button type="button" onClick={() => setOpen((value) => !value)}>إدارة العضوية</button> : null}
      {open && linked ? (
        <div className="membership-compact-actions">
          <p>الجلسات النشطة: {summary?.activeSessions ?? 0}</p>
          <p>آخر تعرف ناجح: {summary?.lastVerifiedAtUtc ? new Date(summary.lastVerifiedAtUtc).toLocaleString() : "—"}</p>
          <button type="button" onClick={() => void revoke()}>إلغاء الجلسات</button>
          {canUnlink ? <button type="button" onClick={() => void unlink()}>إلغاء الربط</button> : null}
        </div>
      ) : null}
      {exceptionPending ? <p>مسجّل باستثناء بانتظار موافقة المدير العام.</p> : null}
      {exceptionPending && canApproveException ? <button type="button" onClick={() => void fetch(`/api/customers/${customerId}/membership/exception/approve`, { method: "POST", credentials: "same-origin", headers: deviceHeaders(session) }).then((response) => { if (response.ok) setExceptionPending(false); })}>موافقة المدير العام</button> : null}
      {notice ? <p role="status">{notice}</p> : null}
    </section>
  );
}
