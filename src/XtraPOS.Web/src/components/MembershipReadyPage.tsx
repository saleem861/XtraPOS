import { useEffect, useState } from "react";
import { pendingEnrollmentKey } from "../membership/newCustomerDraft";

export function MembershipReadyPage() {
  const pending = new URLSearchParams(window.location.search).get("pending");
  const [stored, setStored] = useState(false);
  useEffect(() => {
    if (!pending) return;
    sessionStorage.setItem(pendingEnrollmentKey, pending);
    setStored(true);
  }, [pending]);
  if (pending) {
    return (
      <section className="card" dir="rtl">
        <h1>التحقق جاهز للحفظ</h1>
        <p>لم يُنشأ الزبون بعد. ارجع إلى نموذج الزبون واضغط حفظ لربط العضوية.</p>
        {stored ? <p>تم الاحتفاظ بنتيجة التحقق لهذه الجلسة.</p> : null}
      </section>
    );
  }
  return (
    <section className="card" dir="rtl">
      <h1>تم حفظ العضوية</h1>
      <p>اكتمل ربط الحساب وإنشاء الجلسة. لا حاجة للعودة إلى حفظ الزبون.</p>
    </section>
  );
}
