import { SETUP_STEPS, type SetupStepId, type SetupStepState } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

const STATE_LABELS: Record<SetupStepState, string> = {
  pending: "بانتظار",
  active: "جاري",
  done: "تم",
  error: "تعذر",
};

interface SetupProgressScreenProps {
  steps: Record<SetupStepId, SetupStepState>;
  error: string;
  running: boolean;
  onRetry: () => void;
  onEdit: () => void;
  onRefresh: () => void;
}

export function SetupProgressScreen({
  steps,
  error,
  running,
  onRetry,
  onEdit,
  onRefresh,
}: SetupProgressScreenProps) {
  const alreadyConfigured = error === "تم إعداد هذا الفرع من قبل.";

  return (
    <section className="card" aria-busy={running}>
      <ScreenHeader title="إعداد الفرع" />
      <header className="card-header">
        <p>{running ? "يتم الآن تجهيز الفرع" : error ? "لم يكتمل الإعداد" : "اكتمل فحص الإعداد"}</p>
      </header>
      <ol className="progress-list" aria-live="polite">
        {SETUP_STEPS.map((step) => {
          const state = steps[step.id];
          return (
            <li key={step.id} className={`progress-item is-${state}`}>
              <span className="progress-mark" aria-hidden="true" />
              <span className="progress-label">{step.label}</span>
              <span className="progress-state">{STATE_LABELS[state]}</span>
            </li>
          );
        })}
      </ol>
      {error ? <p className="form-error">{error}</p> : null}
      {!running && error ? (
        <div className="wizard-actions">
          {alreadyConfigured ? (
            <button className="primary-button" type="button" onClick={onRefresh}>
              تحديث الحالة
            </button>
          ) : (
            <button className="primary-button" type="button" onClick={onRetry}>
              إعادة المحاولة
            </button>
          )}
          <button className="ghost-button" type="button" onClick={onEdit}>
            العودة لتعديل البيانات
          </button>
        </div>
      ) : null}
    </section>
  );
}
