import { useState, type FormEvent } from "react";
import type { AdminSetupInput, DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

interface AdminSetupScreenProps {
  session: DeviceSession;
  pending: boolean;
  error: string;
  onSubmit: (input: AdminSetupInput) => void;
}

const USERNAME_PATTERN = /^[\p{L}\p{N}._-]{3,100}$/u;

const emptyAdmin: AdminSetupInput = {
  username: "",
  displayName: "",
  fullName: "",
  employeeNumber: "",
  password: "",
  confirmPassword: "",
};

export function AdminSetupScreen({ session, pending, error, onSubmit }: AdminSetupScreenProps) {
  const [form, setForm] = useState<AdminSetupInput>(emptyAdmin);
  const [fieldError, setFieldError] = useState("");

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) {
      return;
    }

    if (!USERNAME_PATTERN.test(form.username.trim())) {
      setFieldError("يرجى إدخال اسم مستخدم صالح (من 3 إلى 100 حرفاً).");
      return;
    }

    if (!form.displayName.trim()) {
      setFieldError("يرجى إدخال الاسم الظاهر");
      return;
    }

    if (form.password.length < 8) {
      setFieldError("كلمة المرور يجب أن تكون 8 أحرف على الأقل.");
      return;
    }

    if (form.password !== form.confirmPassword) {
      setFieldError("تأكيد كلمة المرور غير مطابق.");
      return;
    }

    setFieldError("");
    onSubmit(form);
  }

  return (
    <section className="card">
      <ScreenHeader title="إنشاء حساب مدير النظام" context={session.branchName} />
      <header className="card-header">
        <p>
          {session.businessName} — {session.branchName}
        </p>
      </header>
      <form className="form" onSubmit={handleSubmit}>
        <label className="field">
          <span>اسم المستخدم</span>
          <input
            value={form.username}
            onChange={(event) => setForm({ ...form, username: event.target.value })}
            maxLength={100}
            autoComplete="off"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>الاسم الظاهر</span>
          <input
            value={form.displayName}
            onChange={(event) => setForm({ ...form, displayName: event.target.value })}
            maxLength={150}
            autoComplete="off"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>الاسم الكامل</span>
          <input
            value={form.fullName}
            onChange={(event) => setForm({ ...form, fullName: event.target.value })}
            maxLength={300}
            autoComplete="off"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>رقم الموظف</span>
          <input
            value={form.employeeNumber}
            onChange={(event) => setForm({ ...form, employeeNumber: event.target.value })}
            maxLength={50}
            autoComplete="off"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>كلمة المرور</span>
          <input
            type="password"
            value={form.password}
            onChange={(event) => setForm({ ...form, password: event.target.value })}
            maxLength={128}
            autoComplete="new-password"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>تأكيد كلمة المرور</span>
          <input
            type="password"
            value={form.confirmPassword}
            onChange={(event) => setForm({ ...form, confirmPassword: event.target.value })}
            maxLength={128}
            autoComplete="new-password"
            disabled={pending}
          />
        </label>
        {fieldError || error ? <p className="form-error">{fieldError || error}</p> : null}
        <button className="primary-button" type="submit" disabled={pending}>
          {pending ? "جاري الإنشاء" : "إنشاء حساب مدير النظام"}
        </button>
      </form>
    </section>
  );
}
