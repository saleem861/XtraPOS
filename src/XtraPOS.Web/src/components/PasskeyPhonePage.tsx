import { useEffect, useState } from "react";

type View = {
  customerName: string;
  maskedPhone: string;
  status: string;
  secondsLeft: number;
  phoneVerified: boolean;
  hasPasskey: boolean;
};

export function PasskeyPhonePage({ pathname }: { pathname: string }) {
  const enroll = pathname.startsWith("/passkey/enroll/");
  const token = decodeURIComponent(pathname.split("/").filter(Boolean)[2] ?? "");
  const [view, setView] = useState<View | null>(null);
  const [message, setMessage] = useState("");
  const [left, setLeft] = useState(0);
  const [agreed, setAgreed] = useState(false);

  useEffect(() => {
    void fetch(`/api/passkey/${token}`).then(async (response) => {
      const body = await response.json() as View & { message?: string };
      if (!response.ok) {
        setMessage(body.message ?? "انتهت صلاحية الطلب.");
        return;
      }
      setView(body);
      setLeft(body.secondsLeft);
    }).catch(() => setMessage("خدمة التحقق غير متاحة. لم يتم تأكيد الهوية."));
  }, [token]);

  useEffect(() => {
    const timer = window.setInterval(() => setLeft((value) => Math.max(0, value - 1)), 1000);
    return () => window.clearInterval(timer);
  }, []);

  async function activate() {
    if (!agreed || !view) return;
    setMessage("");
    const optionsResponse = await fetch(enroll ? `/api/passkey/${token}/registration/options` : `/api/passkey/${token}/assertion/options`, { method: "POST", headers: { Origin: location.origin } });
    const options = await optionsResponse.json() as { message?: string; challenge?: string; rpId?: string; userHandle?: string; credentials?: string[] };
    if (!optionsResponse.ok || !options.challenge || !options.rpId) {
      setMessage(options.message ?? "تعذر بدء Passkey.");
      return;
    }
    const challenge = decode(options.challenge);
    try {
      if (enroll) {
        const credential = await navigator.credentials.create({
          publicKey: {
            challenge,
            rp: { name: "XtraPOS", id: options.rpId },
            user: { id: decode(options.userHandle ?? ""), name: view.maskedPhone, displayName: view.customerName },
            pubKeyCredParams: [{ type: "public-key", alg: -7 }],
            authenticatorSelection: { residentKey: "required", userVerification: "required" },
            timeout: 60000,
            attestation: "none",
          },
        }) as PublicKeyCredential | null;
        const response = credential?.response as AuthenticatorAttestationResponse | undefined;
        if (!credential || !response) return;
        const saved = await fetch(`/api/passkey/${token}/registration`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Origin: location.origin },
          body: JSON.stringify({ clientDataJSON: encode(response.clientDataJSON), attestationObject: encode(response.attestationObject) }),
        });
        const body = await saved.json() as { message?: string };
        setMessage(saved.ok ? "تم تفعيل Passkey لهذا الحساب." : body.message ?? "فشل التحقق.");
      } else {
        const credential = await navigator.credentials.get({
          publicKey: {
            challenge,
            rpId: options.rpId,
            allowCredentials: (options.credentials ?? []).map((id) => ({ type: "public-key" as const, id: decode(id) })),
            userVerification: "required",
            timeout: 60000,
          },
        }) as PublicKeyCredential | null;
        const response = credential?.response as AuthenticatorAssertionResponse | undefined;
        if (!credential || !response) return;
        const saved = await fetch(`/api/passkey/${token}/assertion`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Origin: location.origin },
          body: JSON.stringify({
            credentialId: encode(credential.rawId),
            clientDataJSON: encode(response.clientDataJSON),
            authenticatorData: encode(response.authenticatorData),
            signature: encode(response.signature),
          }),
        });
        const body = await saved.json() as { message?: string };
        setMessage(saved.ok ? "تم التحقق بنجاح." : body.message ?? "فشل التحقق.");
      }
    } catch {
      setMessage("فشل التحقق على هذا الجهاز.");
    }
  }

  return (
    <main className="passkey-phone" dir="rtl">
      <h1>{enroll ? "تفعيل Passkey" : "التحقق من الهوية"}</h1>
      {view ? <p>الحساب: {view.customerName}</p> : null}
      {view ? <p>الهاتف: {view.maskedPhone}</p> : null}
      <p>{left > 0 ? `ينتهي خلال ${left} ثانية` : "انتهت صلاحية الطلب"}</p>
      {message ? <p className="status-error">{message}</p> : null}
      {enroll ? <label><input type="checkbox" checked={agreed} onChange={(event) => setAgreed(event.target.checked)} />أوافق على حفظ Passkey لهذا الحساب</label> : null}
      <button type="button" disabled={left <= 0 || (enroll && !agreed)} onClick={() => void activate()}>{enroll ? "إنشاء Passkey" : "تأكيد بهاتفي"}</button>
    </main>
  );
}

function decode(value: string) {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(padded);
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

function encode(value: ArrayBuffer) {
  const bytes = new Uint8Array(value);
  let text = "";
  bytes.forEach((byte) => { text += String.fromCharCode(byte); });
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}
