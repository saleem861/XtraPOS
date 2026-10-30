import { lazy, Suspense } from "react";
import App from "./App";
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
