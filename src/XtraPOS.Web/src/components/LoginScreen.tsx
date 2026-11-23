import { useEffect, useState, type FormEvent } from "react";
import type { DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

interface LoginScreenProps {
  session: DeviceSession;
  pending: boolean;
  error: string;
  onSubmit: (username: string, password: string) => void;
}

export function LoginScreen({ session, pending, error, onSubmit }: LoginScreenProps) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [updateMessage, setUpdateMessageState] = useState<{ text: string; ok: boolean } | null>(null);
  const setUpdateMessage = (text: string, ok = false) => setUpdateMessageState(text ? { text, ok } : null);
  const [updating, setUpdating] = useState(false);
  const [updateRequest, setUpdateRequest] = useState<string | null>(null);
  const [updateStage, setUpdateStage] = useState("");
  const [updateStep, setUpdateStep] = useState(0);
  const ringShare = updateStep / UPDATE_STAGES.length;

  // The separate updater task reports each stage in its status file; the screen follows it.
  useEffect(() => {
    if (!updating || !updateRequest) return;
    const started = Date.now();
    let done = false;
    let picked = false;
    const finish = (message: string, ok = false) => {
      done = true;
      setUpdating(false);
      setUpdateRequest(null);
      setUpdateMessage(message, ok);
    };
    const timer = window.setInterval(() => {
      if (done) return;
      const elapsed = Math.floor((Date.now() - started) / 1000);
      if (elapsed >= UPDATER_SECONDS) {
        finish("لم يكتمل التحديث خلال 5 دقائق. راجع سجل برنامج التحديث على الجهاز.");
        return;
      }
      // برنامج التحديث يعمل كل دقيقة؛ إن لم يلتقط الطلب خلال دقيقتين فهو لا يعمل على هذا الجهاز.
      if (!picked && elapsed >= PICKUP_SECONDS) {
        finish("برنامج التحديث لا يعمل على هذا الجهاز. نزّل برنامج الإعداد من موقع الإعداد وشغّله مرة واحدة ليعيد تثبيته، ثم أعد المحاولة.");
        return;
      }
      void fetch("/api/local-update/status", { cache: "no-store" })
        .then(async (response) => {
          if (!response.ok || done) return;
          const body = await response.json() as UpdateStatusResponse;
          const status = body.status;
          if (!status || status.requestId !== updateRequest) {
            setUpdateStage(body.updaterAlive ? "بانتظار برنامج التحديث" : "بانتظار برنامج التحديث (يبدأ خلال دقيقة)");
            return;
          }
          picked = true;
          if (status.state === "Succeeded") {
            done = true;
            setUpdateStep(UPDATE_STAGES.length);
            setUpdateStage("تم التحديث، جاري فتح النسخة الجديدة");
            window.location.reload();
            return;
          }
          if (status.state === "UpToDate") {
            finish(`التطبيق محدّث إلى آخر نسخة (${status.to || body.installed}).`, true);
            return;
          }
          if (status.state === "Failed") {
            finish(`فشل التحديث وبقيت النسخة السابقة تعمل. السبب: ${status.detail || "غير معروف"}`);
            return;
          }
          const step = UPDATE_STAGES.indexOf(status.stage) + 1;
          if (step > 0) setUpdateStep(step);
          setUpdateStage(updateStageText(status.stage, status.to));
        })
        .catch(() => {
          // الخدمة متوقفة أثناء استبدال الملفات.
        });
    }, 2000);
    return () => window.clearInterval(timer);
  }, [updating, updateRequest]);

  async function updateApp() {
    if (updating) return;
    setUpdateMessage("");
    setUpdateStage("");
    setUpdateStep(0);
    setUpdateRequest(null);
    setUpdating(true);
    setUpdateStage("جاري طلب التحديث");
    try {
      const response = await fetch("/api/local-update", { method: "POST" });
      const body = await response.json().catch(() => ({})) as { message?: string; mode?: string; requestId?: string; updaterAlive?: boolean };
      if (!response.ok || body.mode !== "updater" || !body.requestId) {
        setUpdating(false);
        setUpdateMessage(body.message ?? "تعذر تحديث التطبيق.");
        return;
      }
      setUpdateStage(body.updaterAlive === false ? "بانتظار برنامج التحديث (يبدأ خلال دقيقة)" : "بانتظار برنامج التحديث");
      setUpdateRequest(body.requestId);
    } catch {
      setUpdating(false);
      setUpdateMessage("تعذر الاتصال بخدمة التطبيق على هذا الجهاز.");
    }
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) {
      return;
    }

    onSubmit(username, password);
  }

  return (
    <section className="card login-card">
      <ScreenHeader title="تسجيل الدخول" context={session.branchName} />
      <header className="card-header">
        <p>{session.businessName}</p>
      </header>
      <dl className="details compact-details">
        <div>
          <dt>اسم المنشأة</dt>
          <dd>{session.businessName}</dd>
        </div>
        <div>
          <dt>اسم الفرع</dt>
          <dd>{session.branchName}</dd>
        </div>
        <div>
          <dt>اسم الجهاز / الكاونتر</dt>
          <dd>
            {session.deviceName}
            {session.stationName ? ` — ${session.stationName}` : ""}
          </dd>
        </div>
      </dl>
      {updateMessage ? (
        <p className={updateMessage.ok ? "update-result is-ok" : "update-result is-error"} role="status" aria-live="polite">{updateMessage.text}</p>
      ) : null}
      <form className="form" onSubmit={handleSubmit}>
        <label className="field">
          <span>اسم المستخدم</span>
          <input
            value={username}
            onChange={(event) => setUsername(event.target.value)}
            autoComplete="username"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>كلمة المرور</span>
          <input
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
            disabled={pending}
          />
        </label>
        {error ? <p className="form-error">{error}</p> : null}
        <button className="primary-button" type="submit" disabled={pending}>
          {pending ? "جاري الدخول" : "تسجيل الدخول"}
        </button>
        <button className="ghost-button" type="button" disabled={updating} onClick={() => void updateApp()}>
          تحديث التطبيق
        </button>
      </form>
      {updating ? (
        <div className="update-timer" role="status" aria-live="polite">
          <div
            className="update-ring"
            style={{ background: `conic-gradient(var(--teal) ${ringShare * 360}deg, #e6eef0 0deg)` }}
          >
            <span className="is-steps">{updateStep} من {UPDATE_STAGES.length}</span>
          </div>
          <p>{updateStage || "جاري تحديث التطبيق"}</p>
        </div>
      ) : null}
    </section>
  );
}

// The updater may take up to 5 minutes on a slow line; the screen shows stages, not this limit.
const UPDATER_SECONDS = 300;
// The updater task starts every minute; a request not picked up in two minutes means it is not running.
const PICKUP_SECONDS = 120;
const UPDATE_STAGES = ["Checking", "Downloading", "Backup", "Stopping", "Copying", "Starting"];

interface UpdateStatusResponse {
  installed: string;
  updaterAlive: boolean;
  status: { requestId: string; state: string; stage: string; detail: string; from: string; to: string; at: string } | null;
}

export function updateStageText(stage: string, to: string) {
  if (stage === "Checking") return "جاري البحث عن آخر نسخة";
  if (stage === "Downloading") return to ? `جاري تنزيل النسخة ${to}` : "جاري تنزيل آخر نسخة";
  if (stage === "Backup") return "جاري حفظ نسخة احتياطية";
  if (stage === "Stopping") return "جاري إيقاف الخدمة";
  if (stage === "Copying") return "جاري نسخ الملفات";
  if (stage === "Starting") return "جاري تشغيل الخدمة";
  if (stage === "RollingBack") return "فشل التحديث، جاري إرجاع النسخة السابقة";
  return "جاري تحديث التطبيق";
}
