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

  useEffect(() => {
    if (!updating) return;
    const started = Date.now();
    let sawDown = false;
    let finished = false;
    const timer = window.setInterval(() => {
      const left = Math.max(0, UPDATE_SECONDS - Math.floor((Date.now() - started) / 1000));
      setUpdateLeft(left);
      void fetch("/health", { cache: "no-store" })
        .then((response) => {
          if (!response.ok) {
            sawDown = true;
            return;
          }
          if (!finished && (sawDown || Date.now() - started > 8000)) {
            finished = true;
            window.location.reload();
          }
        })
        .catch(() => {
          sawDown = true;
        });
    }, 1000);
    return () => window.clearInterval(timer);
  }, [updating]);

  async function updateApp() {
    if (updating) return;
    setUpdating(true);
    setUpdateLeft(UPDATE_SECONDS);
    try {
      const response = await fetch("/api/local-update", { method: "POST" });
      if (!response.ok) {
        const body = await response.json() as { message?: string };
        setUpdating(false);
        setUpdateMessage(body.message ?? "تعذر تحديث التطبيق.");
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
            style={{ background: `conic-gradient(var(--teal) ${((UPDATE_SECONDS - updateLeft) / UPDATE_SECONDS) * 360}deg, #e6eef0 0deg)` }}
          >
            <span>{updateLeft}</span>
          </div>
          <p>{updateLeft > 0 ? "جاري تحديث التطبيق" : "جاري تشغيل الخادم"}</p>
        </div>
      ) : null}
    </section>
  );
}

const UPDATE_SECONDS = 35;
