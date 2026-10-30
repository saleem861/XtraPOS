import { useEffect, useState } from "react";
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

export function PasskeyQrPanel({ session, customerId, purpose, canReplace, onClose }: {
  session: DeviceSession;
  customerId: string;
  purpose: "enrollment" | "verification";
  canReplace: boolean;
  onClose: () => void;
}) {
  const [status, setStatus] = useState("None");
  const [url, setUrl] = useState("");
  const [image, setImage] = useState("");
  const [left, setLeft] = useState(0);
  const [sessionId, setSessionId] = useState("");
  const [message, setMessage] = useState("");

  useEffect(() => {
    void fetch(`/api/customers/${customerId}/passkey`, { headers: deviceHeaders(session) })
      .then((response) => response.json() as Promise<{ status?: string }>)
      .then((body) => setStatus(body.status ?? "None"))
      .catch(() => setMessage("خدمة Passkey غير متاحة. لم يتم تأكيد الهوية."));
  }, [customerId, session]);

  useEffect(() => {
    if (!url) return;
    void QRCode.toDataURL(url, { margin: 1, width: 240 }).then(setImage);
  }, [url]);

  useEffect(() => {
    if (!sessionId) return;
    const timer = window.setInterval(() => {
      setLeft((value) => Math.max(0, value - 1));
      void fetch(`/api/customers/${customerId}/passkey/sessions/${sessionId}`, { headers: deviceHeaders(session) })
        .then((response) => response.json() as Promise<{ status?: string }>)
        .then((body) => {
          if (body.status === "Verified") {
            setStatus("Pending");
            setMessage("أنشأ الزبون Passkey. أكّد ربط الحساب.");
          } else if (body.status === "Expired" || body.status === "Failed") {
            setMessage(labels[body.status] ?? body.status);
          }
        })
        .catch(() => setMessage("خدمة التحقق غير متاحة. لم يتم تأكيد الهوية."));
    }, 2000);
    return () => window.clearInterval(timer);
  }, [customerId, session, sessionId]);

  async function confirm() {
    const response = await fetch(`/api/customers/${customerId}/passkey/confirm`, { method: "POST", headers: deviceHeaders(session) });
    const body = await response.json() as { message?: string; status?: string };
    setMessage(response.ok ? "تم ربط Passkey بعد موافقة الكاشير." : body.message ?? "تعذر التأكيد.");
    if (response.ok) setStatus("Active");
  }

  async function start() {
    if (purpose === "enrollment" && status === "Active" && !canReplace) {
      setMessage("تعديل Passkey لزبون موجود يحتاج صلاحية الإدارة.");
      return;
    }
    const response = await fetch(`/api/customers/${customerId}/passkey/${purpose}`, { method: "POST", headers: deviceHeaders(session) });
    const body = await response.json() as { message?: string; token?: string; urlPath?: string; sessionId?: string; expiresAt?: string };
    if (!response.ok || !body.urlPath || !body.sessionId) {
      setMessage(body.message ?? "تعذر إنشاء رمز التحقق.");
      return;
    }
    setSessionId(body.sessionId);
    setUrl(`${location.origin}${body.urlPath}`);
    setLeft(body.expiresAt ? Math.max(0, Math.round((Date.parse(body.expiresAt) - Date.now()) / 1000)) : 180);
    setMessage("");
    setStatus("Pending");
  }

  return (
    <section className="passkey-panel" dir="rtl" aria-label={purpose === "enrollment" ? "تفعيل Passkey" : "التحقق من هوية الزبون"}>
      <h2>{purpose === "enrollment" ? "تفعيل Passkey" : "التحقق من هوية الزبون"}</h2>
      <p>الحالة: {labels[status] ?? status}</p>
      <p>يمسح الزبون الرمز بهاتفه. مسح الرمز وحده لا يفعّل الحساب.</p>
      {image ? <img alt="رمز تفعيل Passkey" src={image} /> : null}
      {left > 0 ? <p>ينتهي خلال {left} ثانية</p> : null}
      {message ? <p>{message}</p> : null}
      <button type="button" onClick={() => void start()}>{url ? "إعادة توليد الرمز" : "توليد الرمز"}</button>
      {status === "Pending" ? <button type="button" onClick={() => void confirm()}>تأكيد ربط الحساب</button> : null}
      <button type="button" onClick={onClose}>إغلاق</button>
    </section>
  );
}
