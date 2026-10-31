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
  existingBranch: { name: string; code: string } | null;
  onRetry: () => void;
  onEdit: () => void;
  onReplace: () => void;
}

export function SetupProgressScreen({
  steps,
  error,
  running,
  existingBranch,
  onRetry,
  onEdit,
  onReplace,
}: SetupProgressScreenProps) {
  const noticedBranch = existingBranch ?? (error === "تم إعداد هذا الفرع من قبل." ? { name: "", code: "" } : null);
  const branchLabel = [noticedBranch?.name, noticedBranch?.code].filter(Boolean).join(" — ");
  const warning = noticedBranch
    ? `بيانات الفرع موجودة مسبقًا${branchLabel ? `: ${branchLabel}` : ""}. يمكنك استبدال الفرع الحالي بالإعدادات الجديدة، أو الرجوع لتغيير الإعدادات. الاستبدال يزيل إعداد الفرع على هذا الجهاز، ثم ينشئه من جديد بالإعدادات التي أدخلتها.`
    : "";

  return (
    <section className="card" aria-busy={running}>
      <ScreenHeader title="إعداد الفرع" />
      <header className="card-header">
        <p>{running ? "يتم الآن تجهيز الفرع" : noticedBranch ? "هذا الفرع موجود مسبقًا" : error ? "لم يكتمل الإعداد" : "اكتمل فحص الإعداد"}</p>
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
      {warning ? <p className="form-notice">{warning}</p> : null}
      {error && !noticedBranch ? <p className="form-error">{error}</p> : null}
      {!running && noticedBranch ? (
        <div className="wizard-actions">
          <button className="primary-button" type="button" onClick={onReplace}>
            استبدال الفرع
          </button>
          <button className="ghost-button" type="button" onClick={onEdit}>
            تغيير الإعدادات
          </button>
        </div>
      ) : null}
      {!running && error && !noticedBranch ? (
        <div className="wizard-actions">
          <button className="primary-button" type="button" onClick={onRetry}>
            إعادة المحاولة
          </button>
          <button className="ghost-button" type="button" onClick={onEdit}>
            العودة لتعديل البيانات
          </button>
        </div>
      ) : null}
    </section>
  );
}
