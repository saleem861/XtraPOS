import { useEffect, useState } from "react";
import {
  classifyPasskeyError,
  formatPasskeyDiagnostic,
  inspectPhonePasskeyCapability,
  phonePasskeyCreation,
  readPasskeyEnvironment,
  type PasskeyStage,
} from "../passkey/phonePasskey";

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
  const [prepared, setPrepared] = useState<{ challenge?: string; rpId?: string; userHandle?: string; credentials?: string[]; message?: string } | null>(null);
  const [blocked, setBlocked] = useState(false);

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

  useEffect(() => {
    let cancelled = false;
    void inspectPhonePasskeyCapability(readPasskeyEnvironment()).then((failure) => {
      if (cancelled || !failure) return;
      setBlocked(true);
      setMessage(formatPasskeyDiagnostic(failure));
    });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!view || (enroll && !agreed)) return undefined;
    let cancelled = false;
    setPrepared(null);
    void fetch(enroll ? `/api/passkey/${token}/registration/options` : `/api/passkey/${token}/assertion/options`, { method: "POST", headers: { Origin: location.origin } })
      .then(async (response) => {
        const options = await response.json() as { message?: string; challenge?: string; rpId?: string; userHandle?: string; credentials?: string[] };
        if (cancelled) return;
        if (!response.ok || !options.challenge || !options.rpId) {
          const stage: PasskeyStage = enroll ? "registration-options" : "assertion-options";
          setMessage(`${options.message ?? "تعذر بدء Passkey."} [${stage} / HTTP ${response.status}]`);
          return;
        }
        setPrepared(options);
      })
      .catch((error) => {
        if (!cancelled) setMessage(formatPasskeyDiagnostic(classifyPasskeyError(error, enroll ? "registration-options" : "assertion-options")));
      });
    return () => { cancelled = true; };
  }, [agreed, enroll, token, view]);

  async function activate() {
    const challengeText = prepared?.challenge;
    const rpId = prepared?.rpId;
    if (!view || blocked || (enroll && !agreed) || !challengeText || !rpId) return;
    const capability = await inspectPhonePasskeyCapability(readPasskeyEnvironment());
    if (capability) {
      setBlocked(true);
      setMessage(formatPasskeyDiagnostic(capability));
      return;
    }
    setMessage("");
    const stage: PasskeyStage = enroll ? "credentials.create" : "credentials.get";
    try {
      const challenge = decode(challengeText);
      const credential = enroll
        ? await navigator.credentials.create({
          publicKey: phonePasskeyCreation({
            challenge,
            rpId,
            userId: decode(prepared?.userHandle ?? ""),
            userName: view.maskedPhone,
            displayName: view.customerName,
          }),
        }) as PublicKeyCredential | null
        : await navigator.credentials.get({
          publicKey: {
            challenge,
            rpId,
            allowCredentials: (prepared?.credentials ?? []).map((id) => ({ type: "public-key" as const, id: decode(id) })),
            userVerification: "required",
            timeout: 60000,
          },
        }) as PublicKeyCredential | null;
      const response = credential?.response;
      if (!credential || !response) return;
      try {
        const saved = await fetch(enroll ? `/api/passkey/${token}/registration` : `/api/passkey/${token}/assertion`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Origin: location.origin },
          body: JSON.stringify(enroll
            ? {
              clientDataJSON: encode((response as AuthenticatorAttestationResponse).clientDataJSON),
              attestationObject: encode((response as AuthenticatorAttestationResponse).attestationObject),
            }
            : {
              credentialId: encode(credential.rawId),
              clientDataJSON: encode((response as AuthenticatorAssertionResponse).clientDataJSON),
              authenticatorData: encode((response as AuthenticatorAssertionResponse).authenticatorData),
              signature: encode((response as AuthenticatorAssertionResponse).signature),
            }),
        });
        const body = await saved.json() as { message?: string };
        setMessage(saved.ok
          ? (enroll ? "تم تفعيل Passkey لهذا الحساب." : "تم التحقق بنجاح.")
          : `${body.message ?? "فشل التحقق."} [submission / HTTP ${saved.status}]`);
      } catch (error) {
        setMessage(formatPasskeyDiagnostic(classifyPasskeyError(error, "submission")));
      }
    } catch (error) {
      setMessage(formatPasskeyDiagnostic(classifyPasskeyError(error, stage)));
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
      <button type="button" disabled={blocked || left <= 0 || (enroll && !agreed) || !prepared?.challenge} onClick={() => void activate()}>{enroll ? "إنشاء Passkey" : "تأكيد بهاتفي"}</button>
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
