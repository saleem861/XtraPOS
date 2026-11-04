import { useEffect, useRef, useState } from "react";
import { deviceHeaders } from "../api/client";
import type { DeviceSession } from "../types/setup";

interface Created {
  verificationRequestId: string;
  token: string;
  expiresAtUtc: string;
  pollingIntervalMs: number;
}

export const membershipDisplayChannel = "xtrapos-membership-display";

export function MembershipQr({ session, customerId, saleContextId, active, onPhase }: {
  session: DeviceSession;
  customerId: string;
  saleContextId: string;
  active: boolean;
  onPhase?: (status: string, restrictPrices: boolean, customerId?: string) => void;
}) {
  const [enabled, setEnabled] = useState(false);
  const [restrict, setRestrict] = useState(false);
  const [created, setCreated] = useState<Created | null>(null);
  const [status, setStatus] = useState("Pending");
  const [seconds, setSeconds] = useState(60);
  const [displayReady, setDisplayReady] = useState(false);
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
    setStatus("Pending");
    void fetch("/api/pos/membership-verifications", {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({ customerId, saleContextId }),
    }).then(async (response) => {
      if (!response.ok || stop) return;
      const body = await response.json() as Created;
      requestId = body.verificationRequestId;
      setCreated(body);
      setStatus("Pending");
    }).catch(() => setStatus("Offline"));
    return () => {
      stop = true;
      if (requestId) {
        void fetch(`/api/pos/membership-verifications/${requestId}/cancel`, { method: "POST", headers: deviceHeaders(session) });
      }
    };
  }, [enabled, active, customerId, saleContextId, session]);

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
  if (!enabled) return <p className="pos-meta">التحقق من العضوية غير مفعّل على هذا الخادم.</p>;
  if (!created) return <p className="pos-meta">جاري إنشاء رمز العضوية…</p>;
  const verified = status === "Verified";
  return (
    <div className="membership-qr" dir="rtl" aria-label="التحقق من العضوية">
      <strong>{verified ? "تم التحقق من العضوية" : "امسح رمز العضوية"}</strong>
      {status === "Pending" ? <p>{seconds} ث</p> : null}
      {status === "Pending" && !displayReady ? <p dir="ltr">{url}</p> : null}
      {status === "Pending" && displayReady ? <p>الرمز ظاهر على شاشة الزبون.</p> : null}
      {status === "Expired" ? <p>انتهت صلاحية الرمز. اطلب رمزًا جديدًا من الكاشير.</p> : null}
      {status === "Rejected" ? <p>تعذر التحقق من العضوية.</p> : null}
    </div>
  );
}
