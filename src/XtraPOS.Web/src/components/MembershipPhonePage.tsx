import { useEffect, useState } from "react";

const messages: Record<string, string> = {
  MEMBERSHIP_QR_EXPIRED: "انتهت صلاحية الرمز. اطلب رمزًا جديدًا من الكاشير.",
  MEMBERSHIP_WRONG_CUSTOMER: "هذا الحساب لا يطابق الزبون المحدد.",
  MEMBERSHIP_SESSION_REQUIRED: "يلزم تسجيل الدخول إلى العضوية.",
  MEMBERSHIP_SERVER_UNAVAILABLE: "التحقق من العضوية غير متاح.",
};

export function MembershipPhonePage({ pathname }: { pathname: string }) {
  const token = pathname.split("/").filter(Boolean).at(-1) ?? "";
  const unlinked = new URLSearchParams(window.location.search).get("unlinked") === "1";
  const [message, setMessage] = useState(unlinked ? "هذا الحساب غير مرتبط بزبون بعد." : "جارٍ التعرف على العضوية");
  useEffect(() => {
    if (unlinked) return undefined;
    let stop = false;
    void (async () => {
      const statusResponse = await fetch(`/api/membership/verify/${encodeURIComponent(token)}`, { credentials: "same-origin" });
      const csrf = document.cookie.split("; ").find((row) => row.startsWith("xtrapos-membership-csrf="))?.split("=")[1] ?? "";
      const status = await statusResponse.json() as { status?: string; code?: string };
      if (stop) return;
      if (status.status !== "Pending") {
        setMessage(messages[status.code ?? ""] ?? "تعذر التحقق من العضوية.");
        return;
      }
      const complete = await fetch(`/api/membership/verify/${encodeURIComponent(token)}/complete`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "X-Membership-CSRF": decodeURIComponent(csrf) },
      });
      const body = await complete.json() as { status?: string; code?: string; message?: string };
      if (stop) return;
      if (body.code === "MEMBERSHIP_SESSION_REQUIRED") {
        setMessage(messages.MEMBERSHIP_SESSION_REQUIRED);
        return;
      }
      setMessage(body.status === "Verified" ? "تم التعرف على عضويتك بنجاح" : (body.message ?? messages[body.code ?? ""] ?? "تعذر التحقق من العضوية."));
    })().catch(() => { if (!stop) setMessage(messages.MEMBERSHIP_SERVER_UNAVAILABLE); });
    return () => { stop = true; };
  }, [token, unlinked]);
  const login = message === messages.MEMBERSHIP_SESSION_REQUIRED;
  const googleStart = `/api/membership/auth/google/start?return=${encodeURIComponent(pathname)}`;
  return (
    <main dir="rtl" aria-label="عضوية XtraPOS">
      <h1>XtraPOS</h1>
      <p>{message}</p>
      {login ? <p><a href={googleStart}>Google</a> <a href="/api/membership/auth/apple/start">Apple</a></p> : null}
    </main>
  );
}
