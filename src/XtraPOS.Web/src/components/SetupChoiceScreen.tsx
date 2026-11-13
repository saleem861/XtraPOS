import { Building2, MonitorSmartphone, Network } from "lucide-react";
import { useState } from "react";
import type { LocalBranchConfiguration } from "../setup/branchAgentLocal";
import { ScreenHeader } from "./ScreenHeader";

type SetupChoice = "independent" | "point" | "central";

interface SetupChoiceScreenProps {
  configuredBranch?: LocalBranchConfiguration | null;
  onRefresh?: () => void;
  onIndependent: () => void;
  onPoint: () => void;
  onCentral: () => void;
}

const choices: Array<{ id: SetupChoice; title: string; text: string; icon: typeof Building2 }> = [
  { id: "independent", title: "فرع مستقل", text: "منشأة وفرع وكاونتر وقاعدة خاصة وحساب مدير.", icon: Building2 },
  { id: "point", title: "نقطة تابعة لفرع", text: "ربط هذا الجهاز بفرع موجود بعد موافقة مديره.", icon: MonitorSmartphone },
  { id: "central", title: "إدارة مركزية", text: "إعداد الإدارة المركزية دون استبدال قاعدة موجودة.", icon: Network },
];

export function SetupChoiceScreen({
  configuredBranch = null,
  onRefresh,
  onIndependent,
  onPoint,
  onCentral,
}: SetupChoiceScreenProps) {
  const [selected, setSelected] = useState<SetupChoice | "">("");
  const [showChoices, setShowChoices] = useState(false);

  function start() {
    if (selected === "independent") onIndependent();
    if (selected === "point") onPoint();
    if (selected === "central") onCentral();
  }

  if (configuredBranch && !showChoices) {
    const businessLabel = configuredBranch.businessDisplayName?.trim() || configuredBranch.businessName.trim();
    const branchLabel = [configuredBranch.branchName.trim(), configuredBranch.branchCode?.trim()].filter(Boolean).join(" — ");
    const stationLabel = [
      configuredBranch.stationName?.trim(),
      configuredBranch.stationNumber ? `رقم ${configuredBranch.stationNumber}` : "",
    ]
      .filter(Boolean)
      .join(" — ");

    return (
      <section className="card">
        <ScreenHeader title="حالة هذا الجهاز" />
        <header className="card-header">
          <p>تم إعداد هذا الجهاز مسبقًا</p>
        </header>
        <p className="form-notice">هذا الجهاز مرتبط بفرع محلي ومُعدّ للعمل، ولا يلزم إعادة الإعداد الأول.</p>
        <ul className="review-list">
          {businessLabel ? <li>المنشأة: {businessLabel}</li> : null}
          {branchLabel ? <li>الفرع: {branchLabel}</li> : null}
          <li>قاعدة البيانات: {configuredBranch.databaseName}</li>
          <li>الخادم: {configuredBranch.server}</li>
          {stationLabel ? <li>نقطة البيع: {stationLabel}</li> : null}
          {configuredBranch.deviceName?.trim() ? <li>الجهاز: {configuredBranch.deviceName}</li> : null}
        </ul>
        <div className="wizard-actions">
          {onRefresh ? (
            <button className="primary-button" type="button" onClick={onRefresh}>
              تحديث الحالة
            </button>
          ) : null}
          <button className="ghost-button" type="button" onClick={() => setShowChoices(true)}>
            إعداد فرع أو جهاز آخر
          </button>
        </div>
      </section>
    );
  }

  return (
    <section className="setup-welcome">
      <div className="setup-welcome-copy">
        <p className="setup-mark">XtraPOS</p>
        <h1>مرحبًا بك في XtraPOS</h1>
        <p>اختر طريقة إعداد هذا الجهاز للبدء</p>
      </div>
      {configuredBranch ? (
        <div className="form-notice">
          <span>تم إعداد فرع محلي على هذا الجهاز مسبقًا ({configuredBranch.branchName} — {configuredBranch.databaseName}).</span>{" "}
          <button className="ghost-button" type="button" onClick={() => setShowChoices(false)}>
            عرض تفاصيل الجهاز المُعد
          </button>
        </div>
      ) : null}
      <div className="setup-choice-grid">
        {choices.map((choice) => {
          const Icon = choice.icon;
          return (
            <button
              key={choice.id}
              className={selected === choice.id ? "setup-choice-card is-selected" : "setup-choice-card"}
              type="button"
              onClick={() => setSelected(choice.id)}
            >
              <Icon aria-hidden="true" />
              <strong>{choice.title}</strong>
              <span>{choice.text}</span>
            </button>
          );
        })}
      </div>
      <button className="primary-button" type="button" disabled={selected === ""} onClick={start}>البدء بالإعداد</button>
    </section>
  );
}
