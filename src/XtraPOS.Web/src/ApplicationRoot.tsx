import { lazy, Suspense, useEffect } from "react";
import { traceUi } from "./diagnostics/trace";
import App from "./App";
import { MembershipDisplayPage } from "./components/MembershipDisplayPage";
import { MembershipEnrollPage } from "./components/MembershipEnrollPage";
import { MyLogPage } from "./components/MyLogPage";
import { MembershipReadyPage } from "./components/MembershipReadyPage";
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
  useEffect(() => {
    if (!pathname.startsWith("/setup-preview")) traceUi("page", "open", pathname);
    const onClick = (event: MouseEvent) => {
      const target = event.target instanceof Element ? event.target.closest("button, a") : null;
      if (!target) return;
      traceUi("click", target.tagName.toLowerCase(), (target.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 80));
    };
    document.addEventListener("click", onClick);
    return () => document.removeEventListener("click", onClick);
  }, [pathname]);

  if (pathname === "/myLog") {
    return <MyLogPage />;
  }

  if (pathname.startsWith("/membership/enroll/")) {
    return <MembershipEnrollPage pathname={pathname} />;
  }

  if (pathname.startsWith("/membership/verify/")) {
    return <MembershipPhonePage pathname={pathname} />;
  }

  if (pathname === "/membership/display" || pathname === "/membership/ready") {
    return pathname === "/membership/display"
      ? <MembershipDisplayPage counterId={new URLSearchParams(window.location.search).get("counter") ?? undefined} />
      : <MembershipReadyPage />;
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
