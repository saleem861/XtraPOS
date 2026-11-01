import { SETUP_STEPS, type SetupStepId, type SetupStepState } from "../types/setup";
import { OperationProgress } from "./OperationProgress";
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
  onRefresh: () => void;
  onRelink?: () => void;
}

export function SetupProgressScreen({
  steps,
  error,
  running,
  existingBranch,
  onRetry,
  onEdit,
  onRefresh,
  onRelink,
}: SetupProgressScreenProps) {
  const organizationLocked = error.includes("الإعداد الأول اكتمل");
  const noticedBranch = existingBranch ?? (error === "تم إعداد هذا الفرع من قبل." ? { name: "", code: "" } : null);
  const completed = SETUP_STEPS.filter((step) => steps[step.id] === "done").length;
  const active = SETUP_STEPS.find((step) => steps[step.id] === "active");
  const progressLabel = active ? active.label : running ? "يتم الآن تجهيز الفرع" : "تقدم الإعداد";
  const branchLabel = [noticedBranch?.name, noticedBranch?.code].filter(Boolean).join(" — ");
  const warning = noticedBranch
    ? `بيانات الفرع موجودة مسبقًا${branchLabel ? `: ${branchLabel}` : ""}. بياناته محفوظة ولن تُحذف من شاشة الإعداد. يمكنك فتح الفرع الموجود أو الرجوع لتغيير الإعدادات.`
    : "";

  return (
    <section className="card" aria-busy={running}>
      <ScreenHeader title="إعداد الفرع" />
      <header className="card-header">
        <p>{running ? "يتم الآن تجهيز الفرع" : noticedBranch ? "هذا الفرع موجود مسبقًا" : error ? "لم يكتمل الإعداد" : "اكتمل فحص الإعداد"}</p>
      </header>
      {running || completed > 0 ? (
        <OperationProgress label={progressLabel} percent={(completed / SETUP_STEPS.length) * 100} />
      ) : null}
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
      {organizationLocked ? (
        <p className="form-notice">الإعداد الأول اكتمل. المنظمة موجودة ولن تُنشأ مرة أخرى. اربط هذا الجهاز بالفرع الحالي.</p>
      ) : null}
      {error && !noticedBranch && !organizationLocked ? <p className="form-error">{error}</p> : null}
      {!running && organizationLocked ? (
        <div className="wizard-actions">
          <button className="primary-button" type="button" onClick={onRelink}>
            ربط هذا الجهاز
          </button>
        </div>
      ) : null}
      {!running && noticedBranch ? (
        <div className="wizard-actions">
          <button className="primary-button" type="button" onClick={onRefresh}>
            فتح الفرع الموجود
          </button>
          <button className="ghost-button" type="button" onClick={onEdit}>
            تغيير الإعدادات
          </button>
        </div>
      ) : null}
      {!running && error && !noticedBranch && !organizationLocked ? (
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
