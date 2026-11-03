import { lazy, Suspense } from "react";
import App from "./App";
import { MembershipDisplayPage } from "./components/MembershipDisplayPage";
import { MembershipPhonePage } from "./components/MembershipPhonePage";
import { PasskeyPhonePage } from "./components/PasskeyPhonePage";
import { isSetupPreviewEnabled } from "./setup/setupPreview";

const SetupPreview = import.meta.env.DEV
  ? lazy(() => import("./components/SetupPreview").then((module) => ({ default: module.SetupPreview })))
  : null;

export function ApplicationRoot({
  development,
  pathname,
}: {
  development: boolean;
  pathname: string;
}) {
  if (pathname.startsWith("/membership/verify/")) {
    return <MembershipPhonePage pathname={pathname} />;
  }

  if (pathname === "/membership/display" || pathname === "/membership/ready") {
    return pathname === "/membership/display"
      ? <MembershipDisplayPage counterId={new URLSearchParams(window.location.search).get("counter") ?? undefined} />
      : <section className="card" dir="rtl"><h1>تم حفظ العضوية</h1><p>اكتمل ربط الحساب وإنشاء الجلسة. لا حاجة للعودة إلى حفظ الزبون.</p></section>;
  }

  if (pathname.startsWith("/passkey/enroll/") || pathname.startsWith("/passkey/verify/")) {
    return <PasskeyPhonePage pathname={pathname} />;
  }

  if (import.meta.env.DEV && SetupPreview && isSetupPreviewEnabled(development, pathname)) {
    return (
      <Suspense fallback={null}>
        <SetupPreview />
      </Suspense>
    );
  }

  return <App />;
}
