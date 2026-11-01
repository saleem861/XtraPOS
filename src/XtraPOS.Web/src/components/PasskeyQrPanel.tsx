import { useEffect, useRef, useState } from "react";
import QRCode from "qrcode";
import { deviceHeaders } from "../api/client";
import type { DeviceSession } from "../types/setup";

const labels: Record<string, string> = {
  None: "غير مفعل",
  Active: "مفعل",
  Pending: "بانتظار إكمال التفعيل",
  Revoked: "تم إلغاء التفعيل",
  Verified: "تم التحقق بنجاح",
  Expired: "انتهت صلاحية الطلب",
  Failed: "فشل التحقق",
};

export const passkeyVerifiedMessage = "تم التحقق من الزبون بنجاح";
export const passkeyFailedMessage = "فشل التحقق من الزبون";

export function PasskeyQrPanel({ session, customerId, customerSaved, draft, purpose, canReplace, autoStart, onReserved, onEnrolled, onVerified, onFailed, onClose }: {
  session: DeviceSession;
  customerId: string;
  customerSaved: boolean;
  draft?: { name: string; phone: string };
  purpose: "enrollment" | "verification";
  canReplace: boolean;
  autoStart?: boolean;
  onReserved?: (customerId: string) => void;
  onEnrolled?: (sessionId: string) => void;
  onVerified?: () => void;
  onFailed?: () => void;
  onClose: () => void;
}) {
  const [status, setStatus] = useState("None");
  const [url, setUrl] = useState("");
  const [image, setImage] = useState("");
  const [left, setLeft] = useState(0);
  const [boundId, setBoundId] = useState(customerId);
  const [sessionId, setSessionId] = useState("");
  const [message, setMessage] = useState("");
  const reported = useRef(false);
  const started = useRef(false);
  const verifiedRef = useRef(onVerified);
  const enrolledRef = useRef(onEnrolled);
  const failedRef = useRef(onFailed);
  verifiedRef.current = onVerified;
  enrolledRef.current = onEnrolled;
  failedRef.current = onFailed;

  useEffect(() => {
    if (!customerId || !customerSaved) return;
    void fetch(`/api/customers/${customerId}/passkey`, { headers: deviceHeaders(session) })
      .then((response) => response.json() as Promise<{ status?: string }>)
      .then((body) => setStatus(body.status ?? "None"))
      .catch(() => setMessage("خدمة Passkey غير متاحة. لم يتم تأكيد الهوية."));
  }, [customerId, customerSaved, session]);

  useEffect(() => {
    if (!url) return;
    void QRCode.toDataURL(url, { margin: 1, width: 240 }).then(setImage);
  }, [url]);

  useEffect(() => {
    if (!sessionId || !boundId) return;
    const timer = window.setInterval(() => {
      setLeft((value) => Math.max(0, value - 1));
      void fetch(`/api/customers/${boundId}/passkey/sessions/${sessionId}`, { headers: deviceHeaders(session) })
        .then((response) => response.json() as Promise<{ status?: string }>)
        .then((body) => {
          if (body.status === "Verified" && !reported.current) {
            reported.current = true;
            if (purpose === "verification") {
              setStatus("Verified");
              setMessage(passkeyVerifiedMessage);
              verifiedRef.current?.();
            } else {
              setStatus("Active");
              setMessage(passkeyVerifiedMessage);
              enrolledRef.current?.(sessionId);
            }
          } else if (body.status === "Expired" || body.status === "Failed") {
            setStatus(body.status);
            setMessage(passkeyFailedMessage);
            if (!reported.current) {
              reported.current = true;
              failedRef.current?.();
            }
          }
        })
        .catch(() => setMessage("خدمة التحقق غير متاحة. لم يتم تأكيد الهوية."));
    }, 2000);
    return () => window.clearInterval(timer);
  }, [boundId, purpose, session, sessionId]);

  async function start() {
    if (purpose === "enrollment" && status === "Active" && customerSaved && !canReplace) {
      setMessage("تعديل Passkey لزبون موجود يحتاج صلاحية الإدارة.");
      return;
    }
    const savedCustomer = customerSaved && customerId;
    const response = await fetch(savedCustomer ? `/api/customers/${customerId}/passkey/${purpose}` : "/api/customers/passkey/enrollment", {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: savedCustomer ? undefined : JSON.stringify({ customerId: customerId || undefined, name: draft?.name ?? "", phone: draft?.phone ?? "" }),
    });
    const body = await response.json() as { message?: string; token?: string; urlPath?: string; publicUrl?: string; sessionId?: string; expiresAt?: string; customerId?: string };
    if (!response.ok || !body.urlPath || !body.sessionId) {
      setMessage(passkeyFailedMessage);
      if (!reported.current) {
        reported.current = true;
        failedRef.current?.();
      }
      return;
    }
    if (body.customerId) {
      setBoundId(body.customerId);
      onReserved?.(body.customerId);
    }
    setSessionId(body.sessionId);
    setUrl(body.publicUrl || `${location.origin}${body.urlPath}`);
    setLeft(body.expiresAt ? Math.max(0, Math.round((Date.parse(body.expiresAt) - Date.now()) / 1000)) : 180);
    setMessage("");
    setStatus("Pending");
  }

  useEffect(() => {
    if (!autoStart || started.current) return;
    started.current = true;
    void start();
    // The first open is the only automatic generation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function close() {
    if (purpose === "verification" && status !== "Verified" && !reported.current) onFailed?.();
    onClose();
  }

  return (
    <section className="passkey-panel" dir="rtl" aria-label={purpose === "enrollment" ? "توليد Passkey" : "التحقق من هوية الزبون"}>
      <h2>{purpose === "enrollment" ? "توليد Passkey" : "التحقق من هوية الزبون"}</h2>
      <p>الحالة: {labels[status] ?? status}</p>
      {purpose === "verification" && status === "Verified" ? <p>{passkeyVerifiedMessage}</p> : null}
      {purpose === "enrollment" && status === "Active" ? <p>{passkeyVerifiedMessage}</p> : null}
      <p>يمسح الزبون الرمز بهاتفه ويكمل التحقق من الجهاز.</p>
      {image ? <img alt="رمز Passkey" src={image} /> : null}
      {left > 0 ? <p>ينتهي خلال {left} ثانية</p> : null}
      {message ? <p>{message}</p> : null}
      <button type="button" onClick={() => void start()}>{url ? "إعادة توليد الرمز" : "توليد الرمز"}</button>
      <button type="button" onClick={close}>إغلاق</button>
    </section>
  );
}
