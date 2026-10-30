import { Building2, MonitorSmartphone, Network } from "lucide-react";
import { useState } from "react";

type SetupChoice = "independent" | "point" | "central";

interface SetupChoiceScreenProps {
  onIndependent: () => void;
  onPoint: () => void;
  onCentral: () => void;
}

const choices: Array<{ id: SetupChoice; title: string; text: string; icon: typeof Building2 }> = [
  { id: "independent", title: "فرع مستقل", text: "منشأة وفرع وكاونتر وقاعدة خاصة وحساب مدير.", icon: Building2 },
  { id: "point", title: "نقطة تابعة لفرع", text: "ربط هذا الجهاز بفرع موجود بعد موافقة مديره.", icon: MonitorSmartphone },
  { id: "central", title: "إدارة مركزية", text: "إعداد الإدارة المركزية دون استبدال قاعدة موجودة.", icon: Network },
];

export function SetupChoiceScreen({ onIndependent, onPoint, onCentral }: SetupChoiceScreenProps) {
  const [selected, setSelected] = useState<SetupChoice | "">("");

  function start() {
    if (selected === "independent") onIndependent();
    if (selected === "point") onPoint();
    if (selected === "central") onCentral();
  }

  return (
    <section className="setup-welcome">
      <div className="setup-welcome-copy">
        <p className="setup-mark">XtraPOS</p>
        <h1>مرحبًا بك في XtraPOS</h1>
        <p>اختر طريقة إعداد هذا الجهاز للبدء</p>
      </div>
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
