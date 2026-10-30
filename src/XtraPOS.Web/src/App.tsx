import { useEffect, useRef, useState } from "react";
import {
  createBranch,
  createFirstAdmin,
  getCurrentDevice,
  getCurrentUser,
  getSetupStatus,
  changePassword,
  login,
  logout,
  sendHeartbeat,
  SetupClientError,
} from "./api/client";
import { AdminSetupScreen } from "./components/AdminSetupScreen";
import { HomeScreen } from "./components/HomeScreen";
import { LoginScreen } from "./components/LoginScreen";
import { PasswordChangeScreen } from "./components/PasswordChangeScreen";
import { SetupChoiceScreen } from "./components/SetupChoiceScreen";
import { SetupProgressScreen } from "./components/SetupProgressScreen";
import { SetupWizard } from "./components/SetupWizard";
import { SyncStatusBadge } from "./components/SyncStatusBadge";
import { StatusScreen } from "./components/StatusScreen";
import { UnregisteredDeviceScreen } from "./components/UnregisteredDeviceScreen";
import { asDeviceSession } from "./storage/deviceSession";
import { clearPersistedDevice, loadPersistedDevice, savePersistedDevice } from "./storage/deviceConfig";
import { registerSecondaryDevice } from "./setup/secondaryDevice";
import {
  emptyBranchSetup,
  SETUP_STEPS,
  type BranchSetupInput,
  type AdminSetupInput,
  type CurrentUser,
  type DeviceSession,
  type SetupStepId,
  type SetupStepState,
  type SetupStreamEvent,
} from "./types/setup";

type Phase =
  | { kind: "loading" }
  | { kind: "offline" }
  | { kind: "error"; message: string }
  | { kind: "choice" }
  | { kind: "wizard" }
  | {
      kind: "progress";
      steps: Record<SetupStepId, SetupStepState>;
      error: string;
      running: boolean;
    }
  | { kind: "unregistered"; intent?: "join" | "independent" | "central" }
  | { kind: "adminSetup"; session: DeviceSession; pending: boolean; error: string }
  | { kind: "login"; session: DeviceSession; pending: boolean; error: string }
  | { kind: "passwordChange"; session: DeviceSession; user: CurrentUser; pending: boolean; error: string }
  | { kind: "home"; session: DeviceSession; user: CurrentUser; pending: boolean };

function signedInPhase(session: DeviceSession, user: CurrentUser | null): Phase {
  if (!user) {
    return { kind: "login", session, pending: false, error: "" };
  }

  return user.mustChangePassword
    ? { kind: "passwordChange", session, user, pending: false, error: "" }
    : { kind: "home", session, user, pending: false };
}

function pendingSteps(): Record<SetupStepId, SetupStepState> {
  return Object.fromEntries(SETUP_STEPS.map((step) => [step.id, "pending"])) as Record<
    SetupStepId,
    SetupStepState
  >;
}

function markActiveAsError(steps: Record<SetupStepId, SetupStepState>): Record<SetupStepId, SetupStepState> {
  const next = { ...steps };
  for (const step of SETUP_STEPS) {
    if (next[step.id] === "active") {
      next[step.id] = "error";
    }
  }
  return next;
}

function readOnboardingAuthorization() {
  const prefix = "#onboard=";
  if (!window.location.hash.startsWith(prefix) || window.location.hash.length < prefix.length + 32) {
    return "";
  }

  const token = decodeURIComponent(window.location.hash.slice(prefix.length));
  window.history.replaceState(null, "", window.location.pathname + window.location.search);
  return token;
}

export default function App() {
  const [phase, setPhase] = useState<Phase>({ kind: "loading" });
  const [draft, setDraft] = useState<BranchSetupInput>(emptyBranchSetup);
  const setupAuthorization = useRef("");
  const onboardingAuthorization = useRef(readOnboardingAuthorization());
  const runId = useRef(0);

  useEffect(() => {
    void refresh();
  }, []);

  async function refresh() {
    setPhase({ kind: "loading" });
    try {
      const status = await getSetupStatus();
      const storedEarly = status.status === "DatabaseUnavailable" ? await loadPersistedDevice() : null;
      if (status.status === "DatabaseUnavailable" && storedEarly?.kind === "ready") {
        setPhase({ kind: "offline" });
        return;
      }
      if (status.status === "Error" || status.status === "ConfigurationError" || status.status === "DatabaseUnavailable") {
        setPhase({
          kind: "error",
          message: status.message || "تعذر التحقق من الإعداد.",
        });
        return;
      }

      if (status.status === "Configuring") {
        setPhase({ kind: "error", message: "الإعداد قيد التنفيذ. أعد المحاولة بعد قليل." });
        return;
      }

      const stored = await loadPersistedDevice();
      if (stored.kind === "unreadable") {
        setPhase({ kind: "error", message: "تعذر قراءة إعداد هذا الجهاز. أعد المحاولة، أو استخدم إعادة التهيئة لإعادة التسجيل دون حذف بيانات الفرع." });
        return;
      }
      if (status.status === "NotConfigured" || stored.kind === "none") {
        setPhase({ kind: "choice" });
        return;
      }

      const session = stored.session;
      let current: DeviceSession | null;
      try {
        const lookup = await getCurrentDevice(session);
        if (lookup === "revoked") {
          await clearPersistedDevice();
          setPhase({ kind: "choice" });
          return;
        }
        if (lookup === "offline") {
          setPhase({ kind: "offline" });
          return;
        }
        current = lookup;
      } catch {
        setPhase({ kind: "offline" });
        return;
      }

      if (!current) {
        await clearPersistedDevice();
        setPhase({ kind: "choice" });
        return;
      }

      await savePersistedDevice(current);
      void sendHeartbeat(current);

      if (status.status === "ConfiguredButAdminMissing") {
        setPhase({ kind: "adminSetup", session: current, pending: false, error: "" });
        return;
      }

      const user = await getCurrentUser(current);
      setPhase(signedInPhase(current, user));
    } catch {
      setPhase({ kind: "offline" });
    }
  }

  async function startSetup(input: BranchSetupInput) {
    const id = ++runId.current;
    setDraft(input);
    if (input.deviceRole === "CentralOnly") {
      setPhase({
        kind: "error",
        message: "تم تجهيز إعداد الإدارة المركزية. لم يتم تعديل قاعدة الفرع الحالية ولم يُستورد كتالوج.",
      });
      return;
    }
    if (input.deviceRole === "Secondary") {
      try {
        const session = await registerSecondaryDevice(input);
        savePersistedDevice(session);
        setPhase({
          kind: "error",
          message: "تم ربط هذا الجهاز بخادم الفرع. لا تُخزَّن بيانات SQL على هذا الجهاز.",
        });
      } catch (error) {
        const message = error instanceof SetupClientError ? error.message : "تعذر ربط هذا الجهاز.";
        setPhase({ kind: "error", message });
      }
      return;
    }
    setPhase({ kind: "progress", steps: pendingSteps(), error: "", running: true });

    try {
      await createBranch(input, (event) => {
        if (runId.current !== id) {
          return;
        }
        applySetupEvent(event);
      }, setupAuthorization.current);
    } catch (error) {
      if (runId.current !== id) {
        return;
      }

      const message = error instanceof SetupClientError ? error.message : "تعذر إكمال الإعداد.";
      setPhase((current) =>
        current.kind === "progress"
          ? { ...current, running: false, error: message, steps: markActiveAsError(current.steps) }
          : current,
      );
    }
  }

  function applySetupEvent(event: SetupStreamEvent) {
    if (event.type === "completed") {
      const session = asDeviceSession(event.device);
      if (!session) {
        setPhase((current) =>
          current.kind === "progress"
            ? {
                ...current,
                running: false,
                error: "تعذر حفظ تسجيل الجهاز على هذا الجهاز.",
                steps: markActiveAsError(current.steps),
              }
            : current,
        );
        return;
      }

      try {
        savePersistedDevice(session);
      } catch {
        setPhase((current) =>
          current.kind === "progress"
            ? {
                ...current,
                running: false,
                error: "تم الإعداد، لكن تعذر حفظ تسجيل هذا الجهاز محلياً.",
                steps: current.steps,
              }
            : current,
        );
        return;
      }

      setDraft((current) => ({ ...current, password: "", confirmPassword: "" }));
      setPhase({ kind: "login", session, pending: false, error: "" });
      return;
    }

    setPhase((current) => {
      if (current.kind !== "progress") {
        return current;
      }

      if (event.type === "failed") {
        return {
          ...current,
          running: false,
          error: event.message || "تعذر إكمال الإعداد. يمكنك المحاولة مرة أخرى.",
          steps: markActiveAsError(current.steps),
        };
      }

      if (event.type === "progress" && event.step && event.state) {
        return {
          ...current,
          steps: {
            ...current.steps,
            [event.step]: event.state === "succeeded" ? "done" : "active",
          },
        };
      }

      return current;
    });
  }

  async function submitAdmin(input: AdminSetupInput) {
    if (phase.kind !== "adminSetup" || phase.pending) {
      return;
    }

    const session = phase.session;
    setPhase({ kind: "adminSetup", session, pending: true, error: "" });
    try {
      await createFirstAdmin(session, input, setupAuthorization.current);
      setPhase({ kind: "login", session, pending: false, error: "" });
    } catch (error) {
      const message = error instanceof SetupClientError ? error.message : "تعذر إنشاء حساب مدير النظام.";
      setPhase({ kind: "adminSetup", session, pending: false, error: message });
    }
  }

  async function submitLogin(username: string, password: string) {
    if (phase.kind !== "login" || phase.pending) {
      return;
    }

    const session = phase.session;
    setPhase({ kind: "login", session, pending: true, error: "" });
    try {
      const user = await login(session, username, password);
      setPhase(signedInPhase(session, user));
    } catch (error) {
      const message = error instanceof SetupClientError ? error.message : "تعذر تسجيل الدخول.";
      setPhase({ kind: "login", session, pending: false, error: message });
    }
  }

  async function submitPasswordChange(currentPassword: string, newPassword: string, confirmPassword: string) {
    if (phase.kind !== "passwordChange" || phase.pending) {
      return;
    }

    const session = phase.session;
    setPhase({ ...phase, pending: true, error: "" });
    try {
      const user = await changePassword(session, currentPassword, newPassword, confirmPassword);
      setPhase(signedInPhase(session, user));
    } catch (error) {
      const message = error instanceof SetupClientError ? error.message : "تعذر تغيير كلمة المرور.";
      setPhase({ kind: "passwordChange", session, user: phase.user, pending: false, error: message });
    }
  }

  async function submitLogout() {
    if (phase.kind !== "home" || phase.pending) {
      return;
    }

    const session = phase.session;
    setPhase({ ...phase, pending: true });
    try {
      await logout(session);
    } catch {
      // The login screen is still the safe destination if the request fails.
    }
    setPhase({ kind: "login", session, pending: false, error: "" });
  }

  const title = phase.kind === "login" || phase.kind === "home" ? "نقاط البيع" : "الإعداد";

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true" />
          <span className="brand-name">إكسترا</span>
        </div>
        <div id="pos-topbar-slot" className="pos-topbar-slot" />
        <SyncStatusBadge />
        <p className="topbar-title">{title}</p>
      </header>
      <main className="workspace">
        {phase.kind === "loading" ? <StatusScreen title="XtraPOS" message="جاري التحقق من الإعداد" /> : null}
        {phase.kind === "offline" ? (
          <StatusScreen
            title="XtraPOS"
            message="تعذر الاتصال بالخادم. تأكد أن التطبيق يعمل ثم أعد المحاولة."
            actionLabel="إعادة المحاولة"
            onAction={() => void refresh()}
          />
        ) : null}
        {phase.kind === "error" ? (
          <StatusScreen
            title="XtraPOS"
            message={phase.message}
            actionLabel="إعادة المحاولة"
            onAction={() => void refresh()}
          />
        ) : null}
        {phase.kind === "choice" ? (
          <SetupChoiceScreen
            onIndependent={() => {
              setDraft({ ...emptyBranchSetup, deviceRole: "MainBranch", roleLocked: true });
              setPhase({ kind: "wizard" });
            }}
            onPoint={() => setPhase({ kind: "unregistered", intent: "join" })}
            onCentral={() => setPhase({ kind: "unregistered", intent: "central" })}
          />
        ) : null}
        {phase.kind === "wizard" ? (
          <SetupWizard
            value={draft}
            onChange={setDraft}
            onCancel={() => setPhase({ kind: "choice" })}
            onSubmit={(input) => void startSetup(input)}
          />
        ) : null}
        {phase.kind === "progress" ? (
          <SetupProgressScreen
            steps={phase.steps}
            error={phase.error}
            running={phase.running}
            onRetry={() => void startSetup(draft)}
            onEdit={() => setPhase({ kind: "wizard" })}
            onRefresh={() => void refresh()}
          />
        ) : null}
        {phase.kind === "unregistered" ? (
          <UnregisteredDeviceScreen
            intent={phase.intent}
            onboardingAuthorization={onboardingAuthorization.current}
            onCancel={() => setPhase({ kind: "choice" })}
            onRecovered={(session) => {
              savePersistedDevice(session);
              void refresh();
            }}
          />
        ) : null}
        {phase.kind === "adminSetup" ? (
          <AdminSetupScreen
            session={phase.session}
            pending={phase.pending}
            error={phase.error}
            onSubmit={(input) => void submitAdmin(input)}
          />
        ) : null}
        {phase.kind === "login" ? (
          <LoginScreen
            session={phase.session}
            pending={phase.pending}
            error={phase.error}
            onSubmit={(username, password) => void submitLogin(username, password)}
          />
        ) : null}
        {phase.kind === "passwordChange" ? (
          <PasswordChangeScreen
            pending={phase.pending}
            error={phase.error}
            onSubmit={(currentPassword, newPassword, confirmPassword) =>
              void submitPasswordChange(currentPassword, newPassword, confirmPassword)}
          />
        ) : null}
        {phase.kind === "home" ? (
          <HomeScreen
            session={phase.session}
            user={phase.user}
            pending={phase.pending}
            onLogout={() => void submitLogout()}
            onReset={() => {
              clearPersistedDevice();
              setPhase({ kind: "choice" });
            }}
          />
        ) : null}
      </main>
    </div>
  );
}
