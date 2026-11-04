export function MembershipEnrollPage({ pathname }: { pathname: string }) {
  const token = pathname.split("/").filter(Boolean).at(-1) ?? "";
  const start = `/api/membership/auth/google/start?enrollment=${encodeURIComponent(token)}`;
  return (
    <main dir="rtl">
      <h1>تسجيل العضوية</h1>
      <p>أكمل الدخول بحساب Google من هذا الهاتف. لا تُدخل الحساب على جهاز الكاشير.</p>
      <p><a href={start}>المتابعة عبر Google</a></p>
    </main>
  );
}
