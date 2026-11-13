import { useEffect, useRef, useState } from "react";
import QRCode from "qrcode";
import { deviceHeaders } from "../api/client";
import { traceUi } from "../diagnostics/trace";
import { pendingEnrollmentKey } from "../membership/newCustomerDraft";
import { savedMembershipIdentity } from "../membership/savedIdentity";
import type { DeviceSession } from "../types/setup";

interface Summary {
  configured?: boolean;
  activeSessions?: number;
  providers?: string[];
  lastVerifiedAtUtc?: string | null;
  display?: string | null;
}

export function CustomerMembershipSection({ session, customerId, identityKey = "", canUnlink, canExcept = false, exceptionLocked = false, canApproveException = false, pendingEnrollment = false, onBeforeEnroll, onPending, onException }: {
  session: DeviceSession;
  customerId: string;
  identityKey?: string;
  canUnlink: boolean;
  canExcept?: boolean;
  exceptionLocked?: boolean;
  canApproveException?: boolean;
  pendingEnrollment?: boolean;
  onBeforeEnroll?: () => void;
  onPending?: (attemptId: string, display: string) => void;
  onException?: (enabled: boolean) => void;
}) {
  const [summary, setSummary] = useState<Summary | null>(null);
  const [open, setOpen] = useState(false);
  const [notice, setNotice] = useState("");
  const [exceptionPending, setExceptionPending] = useState(false);
  const [withoutMembership, setWithoutMembership] = useState(false);
  const [qrImage, setQrImage] = useState("");
  const [qrToken, setQrToken] = useState("");
  const [qrError, setQrError] = useState("");
  const onPendingRef = useRef(onPending);
  onPendingRef.current = onPending;

  async function startQr() {
    onBeforeEnroll?.();
    setQrError("");
    setQrImage("");
    setQrToken("");
    const response = await fetch("/api/membership/enrollment-qr", {
      method: "POST",
      credentials: "same-origin",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({ customerId: customerId || null }),
    });
    const body = await response.json().catch(() => ({})) as { token?: string; message?: string; enrollUrl?: string };
    if (!response.ok || !body.token) {
      const message = body.message || "تعذر توليد رمز العضوية.";
      setQrError(message);
      traceUi("customer", "enrollment-qr", message);
      return;
    }
    traceUi("customer", "enrollment-qr", "created");
    const url = body.enrollUrl || `${window.location.origin}/membership/enroll/${body.token}`;
    setQrToken(body.token);
    setQrImage(await QRCode.toDataURL(url, { margin: 1, width: 220 }));
  }

  useEffect(() => {
    if (!qrToken) return undefined;
    let alive = true;
    const look = () => {
      void fetch(`/api/membership/enrollment-qr/${encodeURIComponent(qrToken)}`, { credentials: "same-origin", headers: deviceHeaders(session) }).then(async (response) => {
        if (!alive) return;
        const body = await response.json().catch(() => ({})) as { status?: string; display?: string; attemptId?: string };
        traceUi("customer", "enrollment-poll", response.ok ? (body.status ?? "unknown") : String(response.status));
        if (!response.ok || body.status !== "Ready" || !body.attemptId) return;
        const takenResponse = await fetch(`/api/membership/enrollment/attempt/${encodeURIComponent(body.attemptId)}/taken`, { credentials: "same-origin", headers: deviceHeaders(session) });
        const takenBody = takenResponse.ok ? await takenResponse.json().catch(() => ({})) as { taken?: boolean } : null;
        if (!alive) return;
        if (!takenBody) {
          alive = false;
          setNotice("تعذر التحقق من مفتاح التعريف.");
          setQrImage("");
          setQrToken("");
          return;
        }
        if (takenBody.taken) {
          alive = false;
          sessionStorage.removeItem(pendingEnrollmentKey);
          onPendingRef.current?.("", "");
          setNotice("مفتاح التعريف مستخدم بالفعل.");
          setQrImage("");
          setQrToken("");
          return;
        }
        alive = false;
        sessionStorage.setItem(pendingEnrollmentKey, body.attemptId);
        onPendingRef.current?.(body.attemptId, body.display ?? "");
        setNotice("تم التحقق بنجاح");
        setQrImage("");
        setQrToken("");
      }).catch(() => undefined);
    };
    look();
    const timer = window.setInterval(look, 1000);
    return () => { alive = false; window.clearInterval(timer); };
  }, [qrToken, session]);

  useEffect(() => {
    if (!customerId) return;
    let stop = false;
    void fetch(`/api/customers/${customerId}/membership`, { credentials: "same-origin", headers: deviceHeaders(session) }).then(async (response) => {
      if (stop) return;
      if (!response.ok) {
        setNotice("تعذر قراءة حالة العضوية.");
        return;
      }
      setSummary(await response.json() as Summary);
    }).catch(() => { if (!stop) setNotice("تعذر قراءة حالة العضوية."); });
    return () => { stop = true; };
  }, [customerId, identityKey]);

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

  const scannedIdentity = savedMembershipIdentity(identityKey);
  const shownIdentity = scannedIdentity || (customerId ? savedMembershipIdentity(summary?.display) : "");
  const enrollLabel = customerId || pendingEnrollment ? "تعديل عضوية Google/Apple" : "تسجيل عضوية Google/Apple";
  const dialog = qrImage ? (
    <div className="membership-dialog-backdrop" role="presentation">
      <div className="membership-dialog" role="dialog" aria-label="رمز العضوية">
        <h2>امسح رمز العضوية</h2>
        <img src={qrImage} alt="رمز تسجيل العضوية" width={220} height={220} />
        <div>
          <button type="button" onClick={() => void startQr()}>إعادة التوليد</button>
          <button type="button" onClick={() => { setQrImage(""); setQrToken(""); }}>إغلاق</button>
        </div>
      </div>
    </div>
  ) : null;

  if (!customerId) {
    return (
      <section className="membership-compact" aria-label="العضوية الذكية">
        <strong>العضوية الذكية</strong>
        <span>{pendingEnrollment ? "بانتظار الحفظ" : "غير مرتبطة"}</span>
        {pendingEnrollment ? <p>تم التحقق بنجاح</p> : <p>يمكن تسجيل العضوية قبل حفظ الزبون.</p>}
        <div className="membership-register-row">
          <button type="button" onClick={() => void startQr()}>{enrollLabel}</button>
          <label><span>مفتاح التعريف</span><input aria-label="مفتاح التعريف" readOnly value={shownIdentity} /></label>
          {canExcept ? <label className="membership-exception"><input type="checkbox" disabled={exceptionLocked} checked={withoutMembership} onChange={(event) => { const next = event.target.checked; if (next && !window.confirm("سيتم تسجيل الزبون بدون تعريف إلكتروني. هل تؤكد هذا الاستثناء؟")) return; setWithoutMembership(next); onException?.(next); }} /><span>تسجيل الزبون بدون تعريف إلكتروني</span></label> : null}
        </div>
        {qrError ? <p className="form-error" role="status">{qrError}</p> : null}
        {notice ? <p role="status">{notice}</p> : null}
        {dialog}
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
      <div className="membership-register-row">
        <button type="button" onClick={() => void startQr()}>{enrollLabel}</button>
        <label><span>مفتاح التعريف</span><input aria-label="مفتاح التعريف" readOnly value={shownIdentity} /></label>
        {canExcept ? <label className="membership-exception"><input type="checkbox" disabled={exceptionLocked} checked={withoutMembership} onChange={(event) => { const next = event.target.checked; if (next && !window.confirm("سيتم تسجيل الزبون بدون تعريف إلكتروني. هل تؤكد هذا الاستثناء؟")) return; setWithoutMembership(next); onException?.(next); }} /><span>تسجيل الزبون بدون تعريف إلكتروني</span></label> : null}
      </div>
      {qrError ? <p className="form-error" role="status">{qrError}</p> : null}
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
      {dialog}
    </section>
  );
}
