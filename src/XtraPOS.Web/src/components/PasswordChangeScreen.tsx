import { useState, type FormEvent } from "react";
import { ScreenHeader } from "./ScreenHeader";

interface PasswordChangeScreenProps {
  pending: boolean;
  error: string;
  onSubmit: (currentPassword: string, newPassword: string, confirmPassword: string) => void;
}

export function PasswordChangeScreen({ pending, error, onSubmit }: PasswordChangeScreenProps) {
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) {
      return;
    }

    onSubmit(currentPassword, newPassword, confirmPassword);
  }

  return (
    <section className="card login-card">
      <ScreenHeader title="تغيير كلمة المرور" />
      <header className="card-header">
        <p>يجب استبدال كلمة المرور المؤقتة قبل متابعة العمل.</p>
      </header>
      <form className="form" onSubmit={handleSubmit}>
        <label className="field">
          <span>كلمة المرور الحالية</span>
          <input
            type="password"
            value={currentPassword}
            onChange={(event) => setCurrentPassword(event.target.value)}
            autoComplete="current-password"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>كلمة المرور الجديدة</span>
          <input
            type="password"
            value={newPassword}
            onChange={(event) => setNewPassword(event.target.value)}
            autoComplete="new-password"
            disabled={pending}
          />
        </label>
        <label className="field">
          <span>تأكيد كلمة المرور</span>
          <input
            type="password"
            value={confirmPassword}
            onChange={(event) => setConfirmPassword(event.target.value)}
            autoComplete="new-password"
            disabled={pending}
          />
        </label>
        {error ? <p className="form-error">{error}</p> : null}
        <button className="primary-button" type="submit" disabled={pending}>
          {pending ? "جارٍ الحفظ" : "حفظ كلمة المرور"}
        </button>
      </form>
    </section>
  );
}
