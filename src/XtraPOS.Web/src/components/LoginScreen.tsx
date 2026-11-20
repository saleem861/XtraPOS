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
  const [updateMessage, setUpdateMessage] = useState("");
  const [updating, setUpdating] = useState(false);
  const [updateLeft, setUpdateLeft] = useState(UPDATE_SECONDS);
  const [updateRequest, setUpdateRequest] = useState<string | null>(null);
  const [updateStage, setUpdateStage] = useState("");
  const total = updateRequest ? UPDATER_SECONDS : UPDATE_SECONDS;

  // The separate updater task reports each stage in its status file; the screen follows it.
  useEffect(() => {
    if (!updating || !updateRequest) return;
    const started = Date.now();
    let done = false;
    const finish = (message: string) => {
      done = true;
      setUpdating(false);
      setUpdateRequest(null);
      setUpdateMessage(message);
    };
    const timer = window.setInterval(() => {
      if (done) return;
      const left = Math.max(0, UPDATER_SECONDS - Math.floor((Date.now() - started) / 1000));
      setUpdateLeft(left);
      if (left === 0) {
        finish("لم يكتمل التحديث خلال 5 دقائق. راجع سجل برنامج التحديث على الجهاز.");
        return;
      }
      void fetch("/api/local-update/status", { cache: "no-store" })
        .then(async (response) => {
          if (!response.ok || done) return;
          const body = await response.json() as UpdateStatusResponse;
          const status = body.status;
          if (!status || status.requestId !== updateRequest) {
            setUpdateStage("بانتظار برنامج التحديث");
            return;
          }
          if (status.state === "Succeeded") {
            done = true;
            window.location.reload();
            return;
          }
          if (status.state === "UpToDate") {
            finish(`التطبيق محدّث إلى آخر نسخة (${status.to || body.installed}).`);
            return;
          }
          if (status.state === "Failed") {
            finish(`فشل التحديث وبقيت النسخة السابقة تعمل. السبب: ${status.detail || "غير معروف"}`);
            return;
          }
          setUpdateStage(updateStageText(status.stage, status.to));
        })
        .catch(() => {
          // الخدمة متوقفة أثناء استبدال الملفات.
        });
    }, 2000);
    return () => window.clearInterval(timer);
  }, [updating, updateRequest]);

  useEffect(() => {
    if (!updating || updateRequest) return;
    const started = Date.now();
    let sawDown = false;
    let finished = false;
    const timer = window.setInterval(() => {
      const left = Math.max(0, UPDATE_SECONDS - Math.floor((Date.now() - started) / 1000));
      setUpdateLeft(left);
      if (left === 0 && !sawDown && !finished) {
        finished = true;
        setUpdating(false);
        setUpdateMessage("لم يبدأ التحديث على هذا الجهاز. ثبّت برنامج التحديث مرة واحدة ثم أعد المحاولة.");
        return;
      }
      void fetch("/health", { cache: "no-store" })
        .then((response) => {
          if (!response.ok) {
            sawDown = true;
            return;
          }
          if (!finished && sawDown && response.ok) {
            finished = true;
            window.location.reload();
          }
        })
        .catch(() => {
          sawDown = true;
        });
    }, 1000);
    return () => window.clearInterval(timer);
  }, [updating, updateRequest]);

  async function updateApp() {
    if (updating) return;
    setUpdateMessage("");
    setUpdateStage("");
    setUpdateRequest(null);
    setUpdating(true);
    setUpdateLeft(UPDATE_SECONDS);
    try {
      const response = await fetch("/api/local-update", { method: "POST" });
      const body = await response.json().catch(() => ({})) as { message?: string; mode?: string; requestId?: string };
      if (!response.ok) {
        setUpdating(false);
        setUpdateMessage(body.message ?? "تعذر تحديث التطبيق.");
        return;
      }
      if (body.mode === "updater" && body.requestId) {
        setUpdateLeft(UPDATER_SECONDS);
        setUpdateStage("بانتظار برنامج التحديث");
        setUpdateRequest(body.requestId);
      }
    } catch {
      // إعادة التشغيل تقطع الاتصال، والمؤقت يكمل ثم يحدّث الصفحة.
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
        {updateMessage ? <p className="form-error">{updateMessage}</p> : null}
      </form>
      {updating ? (
        <div className="update-timer" role="status" aria-live="polite">
          <div
            className="update-ring"
            style={{ background: `conic-gradient(var(--teal) ${((total - updateLeft) / total) * 360}deg, #e6eef0 0deg)` }}
          >
            <span>{updateLeft}</span>
          </div>
          <p>{updateRequest ? updateStage : updateLeft > 0 ? "جاري تحديث التطبيق" : "جاري تشغيل الخادم"}</p>
        </div>
      ) : null}
    </section>
  );
}

const UPDATE_SECONDS = 90;
const UPDATER_SECONDS = 300;

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
