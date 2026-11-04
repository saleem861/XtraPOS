import { useEffect, useState } from "react";

interface Summary {
  configured?: boolean;
  activeSessions?: number;
  providers?: string[];
  lastVerifiedAtUtc?: string | null;
}

interface MembershipConfig {
  enabled?: boolean;
  googleLoginEnabled?: boolean;
  appleLoginEnabled?: boolean;
}

export function CustomerMembershipSection({ customerId, canUnlink, canExcept = false, canApproveException = false, pendingEnrollment = false, onBeforeEnroll, onException }: {
  customerId: string;
  canUnlink: boolean;
  canExcept?: boolean;
  canApproveException?: boolean;
  pendingEnrollment?: boolean;
  onBeforeEnroll?: () => void;
  onException?: (enabled: boolean) => void;
}) {
  const [summary, setSummary] = useState<Summary | null>(null);
  const [config, setConfig] = useState<MembershipConfig | null>(null);
  const [open, setOpen] = useState(false);
  const [notice, setNotice] = useState("");
  const [exceptionPending, setExceptionPending] = useState(false);
  const [withoutMembership, setWithoutMembership] = useState(false);

  useEffect(() => {
    let stop = false;
    void fetch("/api/membership/config").then(async (response) => {
      if (!stop && response.ok) setConfig(await response.json() as MembershipConfig);
    }).catch(() => undefined);
    return () => { stop = true; };
  }, []);

  useEffect(() => {
    if (!customerId) return;
    let stop = false;
    void fetch(`/api/customers/${customerId}/membership`, { credentials: "same-origin" }).then(async (response) => {
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
    const response = await fetch(`/api/customers/${customerId}/membership/revoke-all`, { method: "POST", credentials: "same-origin" });
    setNotice(response.ok ? "تم إلغاء الجلسات النشطة." : "تعذر إلغاء الجلسات.");
    if (response.ok) {
      const next = await fetch(`/api/customers/${customerId}/membership`, { credentials: "same-origin" });
      if (next.ok) setSummary(await next.json() as Summary);
    }
  }

  async function unlink() {
    const response = await fetch(`/api/customers/${customerId}/membership/unlink`, { method: "POST", credentials: "same-origin" });
    setNotice(response.ok ? "تم إلغاء ربط العضوية." : "تعذر إلغاء الربط.");
    if (response.ok) setSummary({ configured: summary?.configured, activeSessions: 0, providers: [] });
  }

  useEffect(() => {
    if (!customerId) return;
    void fetch(`/api/customers/${customerId}/membership/exception`, { credentials: "same-origin" }).then(async (response) => {
      if (!response.ok) return;
      const body = await response.json() as { pending?: boolean };
      setExceptionPending(Boolean(body.pending));
    }).catch(() => undefined);
  }, [customerId]);

  if (!customerId) {
    const google = Boolean(config?.googleLoginEnabled);
    const apple = Boolean(config?.appleLoginEnabled);
    return (
      <section className="membership-compact" aria-label="العضوية الذكية">
        <strong>العضوية الذكية</strong>
        <span>{pendingEnrollment ? "بانتظار الحفظ" : "غير مرتبطة"}</span>
        {pendingEnrollment ? <p>تم التحقق. اضغط حفظ لإنشاء الزبون وربط العضوية.</p> : <p>يمكن تسجيل العضوية قبل حفظ الزبون.</p>}
        {!pendingEnrollment && google ? <a href="/api/membership/auth/google/start" onClick={() => onBeforeEnroll?.()}>تسجيل العضوية — Google</a> : null}
        {!pendingEnrollment && apple ? <a href="/api/membership/auth/apple/start" onClick={() => onBeforeEnroll?.()}>تسجيل العضوية — Apple</a> : null}
        {canExcept ? <label><input type="checkbox" checked={withoutMembership} onChange={(event) => { const next = event.target.checked; if (next && !window.confirm("سيتم تسجيل الزبون بدون تعريف إلكتروني. هل تؤكد هذا الاستثناء؟")) return; setWithoutMembership(next); onException?.(next); }} /> تسجيل الزبون بدون تعريف إلكتروني</label> : null}
      </section>
    );
  }

  const enabled = Boolean(config?.enabled);
  if (!enabled) {
    if (!canUnlink) return null;
    return (
      <section className="membership-compact" aria-label="العضوية الذكية">
        <strong>العضوية الذكية</strong>
        <span>غير مفعّلة</span>
      </section>
    );
  }

  const providers = summary?.providers ?? [];
  const linked = providers.length > 0;
  const google = Boolean(config?.googleLoginEnabled);
  const apple = Boolean(config?.appleLoginEnabled);
  const providerReady = google || apple;

  return (
    <section className={`membership-compact${linked ? " is-linked" : ""}`} aria-label="العضوية الذكية">
      <strong>العضوية الذكية</strong>
      <span>{linked ? "مرتبطة" : "غير مرتبطة"}</span>
      {linked ? providers.map((provider) => <em key={provider}>{provider}</em>) : null}
      {linked ? <button type="button" onClick={() => setOpen((value) => !value)}>إدارة العضوية</button> : null}
      {!linked && providerReady ? <button type="button" onClick={() => setOpen((value) => !value)}>تسجيل العضوية</button> : null}
      {!linked && !providerReady ? <p>تسجيل العضوية غير متاح حالياً</p> : null}
      {open && !linked ? (
        <div className="membership-compact-actions">
          {google ? <a href="/api/membership/auth/google/start">Google</a> : null}
          {apple ? <a href="/api/membership/auth/apple/start">Apple</a> : null}
        </div>
      ) : null}
      {open && linked ? (
        <div className="membership-compact-actions">
          <p>الجلسات النشطة: {summary?.activeSessions ?? 0}</p>
          <p>آخر تعرف ناجح: {summary?.lastVerifiedAtUtc ? new Date(summary.lastVerifiedAtUtc).toLocaleString() : "—"}</p>
          <button type="button" onClick={() => void revoke()}>إلغاء الجلسات</button>
          {canUnlink ? <button type="button" onClick={() => void unlink()}>إلغاء الربط</button> : null}
        </div>
      ) : null}
      {exceptionPending ? <p>مسجّل باستثناء بانتظار موافقة المدير العام.</p> : null}
      {exceptionPending && canApproveException ? <button type="button" onClick={() => void fetch(`/api/customers/${customerId}/membership/exception/approve`, { method: "POST", credentials: "same-origin" }).then((response) => { if (response.ok) setExceptionPending(false); })}>موافقة المدير العام</button> : null}
      {notice ? <p role="status">{notice}</p> : null}
    </section>
  );
}
