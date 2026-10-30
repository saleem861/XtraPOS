import { useState } from "react";
import { deviceHeaders } from "../api/client";
import { centralSetupPayload } from "../setup/centralSetup";
import { AppNav } from "./AppNav";
import { SetupWizard } from "./SetupWizard";
import { ScreenHeader } from "./ScreenHeader";
import { emptyBranchSetup, type BranchSetupInput, type DeviceSession } from "../types/setup";

interface CentralSetupScreenProps {
  session: DeviceSession;
  onHome: () => void;
}

type Page = "settings" | "central" | "wizard" | "progress";
type StageState = "pending" | "active" | "done" | "error";

interface Stage {
  id: string;
  label: string;
  state: StageState;
}

const INITIAL_STAGES: Stage[] = [
  { id: "connect", label: "تم الاتصال بالخادم", state: "pending" },
  { id: "createDatabase", label: "تم إنشاء قاعدة البيانات", state: "pending" },
  { id: "migrations", label: "تم تطبيق بنية XtraPOS Central", state: "pending" },
  { id: "business", label: "تم إنشاء هوية المنشأة", state: "pending" },
  { id: "verify", label: "تم التحقق من القاعدة المركزية", state: "pending" },
];

export function CentralSetupScreen({ session, onHome }: CentralSetupScreenProps) {
  const [page, setPage] = useState<Page>("settings");
  const [draft, setDraft] = useState<BranchSetupInput>({ ...emptyBranchSetup, deviceRole: "CentralOnly" });
  const [stages, setStages] = useState<Stage[]>(INITIAL_STAGES);
  const [error, setError] = useState("");
  const [done, setDone] = useState("");
  const [running, setRunning] = useState(false);

  async function execute(input: BranchSetupInput) {
    setDraft({ ...input, centralPassword: "" });
    setPage("progress");
    setRunning(true);
    setError("");
    setDone("");
    setStages(INITIAL_STAGES.map((stage) => ({ ...stage })));
    const payload = centralSetupPayload(input);
    try {
      const response = await fetch("/api/central/setup", {
        method: "POST",
        credentials: "include",
        headers: {
          ...deviceHeaders(session),
          "Content-Type": "application/json",
          Accept: "application/x-ndjson",
        },
        body: JSON.stringify(payload),
      });
      if (!response.ok || !response.body) {
        const text = await response.text();
        setError(readFailure(text));
        setRunning(false);
        return;
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      let completed = false;
      for (;;) {
        const chunk = await reader.read();
        if (chunk.done) {
          break;
        }
        buffer += decoder.decode(chunk.value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";
        for (const line of lines) {
          completed = applyEvent(line) === "completed" || completed;
        }
      }
      if (buffer.trim()) {
        completed = applyEvent(buffer) === "completed" || completed;
      }
      if (!completed) {
        setError((current) => current || "تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.");
      }
    } catch {
      setError("تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.");
    } finally {
      setRunning(false);
    }
  }

  function applyEvent(line: string) {
    const trimmed = line.trim();
    if (!trimmed) {
      return "open";
    }
    let event: {
      type?: string;
      stage?: string;
      state?: string;
      label?: string;
      message?: string;
      alreadyConfigured?: boolean;
    };
    try {
      event = JSON.parse(trimmed) as typeof event;
    } catch {
      setError("تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.");
      return "failed";
    }
    if (event.type === "failed") {
      setError(event.message || "تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.");
      setStages((current) => current.map((stage) => stage.id === event.stage ? { ...stage, state: "error" } : stage));
      return "failed";
    }
    if (event.type === "completed") {
      setDone(event.alreadyConfigured
        ? "تم إعداد الإدارة المركزية بنجاح. كانت القاعدة معدة مسبقاً، ولم يتم إنشاء سجل جديد."
        : "تم إعداد الإدارة المركزية بنجاح");
      return "completed";
    }
    if (event.type === "progress" && event.stage) {
      setStages((current) => current.map((stage) => {
        if (stage.id !== event.stage) {
          return stage;
        }
        return {
          ...stage,
          label: event.label || stage.label,
          state: event.state === "succeeded" ? "done" : event.state === "failed" ? "error" : "active",
        };
      }));
    }
    return "open";
  }

  if (page === "wizard") {
    return (
      <SetupWizard
        value={draft}
        onChange={setDraft}
        onCancel={() => setPage("central")}
        onSubmit={(input) => void execute(input)}
        initialIndex={1}
        minimumIndex={1}
      />
    );
  }

  if (page === "progress") {
    return (
      <section className="card wizard-card" aria-busy={running}>
        <AppNav onHome={onHome} onBack={() => setPage("wizard")} backDisabled={running} />
        <header className="card-header">
          <h1>{running ? "جارٍ إعداد الإدارة المركزية..." : done ? "تم إعداد الإدارة المركزية بنجاح" : "لم يكتمل إعداد الإدارة المركزية"}</h1>
        </header>
        <ol className="progress-list" aria-live="polite">
          {stages.map((stage) => (
            <li key={stage.id} className={`progress-item is-${stage.state}`}>
              <span className="progress-mark" aria-hidden="true" />
              <span className="progress-label">{stage.state === "done" ? `✓ ${stage.label}` : stage.label}</span>
            </li>
          ))}
        </ol>
        {error ? <p className="form-error">{error}</p> : null}
        {done?.includes("معدة مسبقاً") ? <p role="status">{done}</p> : null}
      </section>
    );
  }

  return (
    <section className="card">
      <ScreenHeader title={page === "settings" ? "الإعدادات" : "الإدارة المركزية"} onBack={page === "settings" ? onHome : () => setPage("settings")} />
      {page === "settings" ? (
        <button className="menu-tile" type="button" onClick={() => setPage("central")}>
          الإدارة المركزية
        </button>
      ) : (
        <button className="menu-tile" type="button" onClick={() => setPage("wizard")}>
          إعداد الإدارة المركزية
        </button>
      )}
    </section>
  );
}

function readFailure(text: string) {
  try {
    const body = JSON.parse(text) as { message?: string };
    return body.message || "تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.";
  } catch {
    return "تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.";
  }
}
