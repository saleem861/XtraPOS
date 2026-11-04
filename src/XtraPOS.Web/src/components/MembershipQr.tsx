import { useEffect, useRef, useState } from "react";
import QRCode from "qrcode";
import { deviceHeaders } from "../api/client";
import { traceUi } from "../diagnostics/trace";
import type { DeviceSession } from "../types/setup";

interface Created {
  verificationRequestId: string;
  token: string;
  expiresAtUtc: string;
  pollingIntervalMs: number;
}

export const membershipDisplayChannel = "xtrapos-membership-display";

export function MembershipQr({ session, customerId, saleContextId, active, onPhase, onDismiss }: {
  session: DeviceSession;
  customerId: string;
  saleContextId: string;
  active: boolean;
  onPhase?: (status: string, restrictPrices: boolean, customerId?: string) => void;
  onDismiss?: () => void;
}) {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [restrict, setRestrict] = useState(false);
  const [created, setCreated] = useState<Created | null>(null);
  const [image, setImage] = useState("");
  const [error, setError] = useState("");
  const [status, setStatus] = useState("Pending");
  const [seconds, setSeconds] = useState(60);
  const [displayReady, setDisplayReady] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const onPhaseRef = useRef(onPhase);
  onPhaseRef.current = onPhase;

  useEffect(() => {
    let stop = false;
    void fetch("/api/membership/config").then(async (response) => {
      if (!response.ok) return;
      const body = await response.json() as { enabled?: boolean; requireVerificationForSpecialPrices?: boolean };
      if (!stop) {
        setEnabled(Boolean(body.enabled));
        setRestrict(Boolean(body.requireVerificationForSpecialPrices));
        onPhaseRef.current?.("Config", Boolean(body.requireVerificationForSpecialPrices));
      }
    }).catch(() => setEnabled(false));
    return () => { stop = true; };
  }, []);

  useEffect(() => {
    const channel = new BroadcastChannel(membershipDisplayChannel);
    channel.onmessage = (event: MessageEvent<{ type?: string }>) => {
      if (event.data?.type === "hello") setDisplayReady(true);
    };
    return () => channel.close();
  }, []);

  useEffect(() => {
    if (!enabled || !active) return undefined;
    let stop = false;
    let requestId = "";
    setCreated(null);
    setImage("");
    setError("");
    setStatus("Pending");
    void fetch("/api/pos/membership-verifications", {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({ customerId: customerId || null, saleContextId }),
    }).then(async (response) => {
      const body = await response.json().catch(() => ({})) as Created & { message?: string };
      if (!response.ok || stop) {
        const message = body.message || "تعذر إنشاء رمز العضوية.";
        setError(message);
        traceUi("pos", "membership-qr", message);
        return;
      }
      requestId = body.verificationRequestId;
      const url = `${window.location.origin}/membership/verify/${body.token}`;
      setImage(await QRCode.toDataURL(url, { margin: 1, width: 220 }));
      setCreated(body);
      setStatus("Pending");
      traceUi("pos", "membership-qr", "created");
    }).catch(() => { setStatus("Offline"); setError("تعذر الاتصال بخادم العضوية."); });
    return () => {
      stop = true;
      if (requestId) {
        void fetch(`/api/pos/membership-verifications/${requestId}/cancel`, { method: "POST", headers: deviceHeaders(session) });
      }
    };
  }, [enabled, active, customerId, saleContextId, session, attempt]);

  useEffect(() => {
    if (!created || status !== "Pending") return undefined;
    const tick = window.setInterval(() => {
      const left = Math.max(0, Math.ceil((Date.parse(created.expiresAtUtc) - Date.now()) / 1000));
      setSeconds(left);
      void fetch(`/api/pos/membership-verifications/${created.verificationRequestId}`, { headers: deviceHeaders(session) })
        .then(async (response) => {
          if (!response.ok) return;
          const body = await response.json() as { status?: string; customerId?: string };
          if (body.status && body.status !== "Pending") {
            setStatus(body.status);
            onPhaseRef.current?.(body.status, restrict, body.customerId);
          }
        })
        .catch(() => setStatus("Offline"));
    }, created.pollingIntervalMs || 1000);
    return () => window.clearInterval(tick);
  }, [created, session, status, restrict]);

  const url = created ? `${window.location.origin}/membership/verify/${created.token}` : "";
  useEffect(() => {
    if (!created) return undefined;
    const channel = new BroadcastChannel(membershipDisplayChannel);
    channel.postMessage({ type: "qr", url, status, customerId, saleContextId, counterId: session.stationId });
    return () => channel.close();
  }, [created, url, status, customerId, saleContextId]);

  if (!active) return null;
  const verified = status === "Verified";
  const title = verified ? "تم التحقق من العضوية" : "امسح رمز العضوية";
  return (
    <div className="membership-dialog-backdrop" role="presentation">
      <div className="membership-dialog" role="dialog" aria-modal="true" aria-label="رمز العضوية">
        <h2>{title}</h2>
        {enabled === false ? <p>التحقق من العضوية غير مفعّل على هذا الخادم.</p> : null}
        {enabled !== false && error ? <p className="status-error">{error}</p> : null}
        {enabled !== false && !error && status === "Pending" && image ? <img src={image} alt="رمز العضوية" width={220} height={220} /> : null}
        {enabled !== false && !error && status === "Pending" && !image ? <p>جاري إنشاء رمز العضوية…</p> : null}
        {enabled !== false && status === "Pending" && created ? <p>{seconds} ث</p> : null}
        {status === "Pending" && displayReady ? <p>الرمز ظاهر على شاشة الزبون.</p> : null}
        {status === "Expired" ? <p>انتهت صلاحية الرمز. اطلب رمزًا جديدًا من الكاشير.</p> : null}
        {status === "Rejected" ? <p>تعذر التحقق من العضوية.</p> : null}
        <div>
          <button type="button" onClick={() => setAttempt((value) => value + 1)}>إعادة التوليد</button>
          <button type="button" onClick={onDismiss}>إغلاق</button>
        </div>
      </div>
    </div>
  );
}
