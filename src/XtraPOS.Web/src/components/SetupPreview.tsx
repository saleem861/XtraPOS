import { useState } from "react";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { SETUP_PREVIEW_BANNER } from "../setup/setupPreview";

export function SetupPreview() {
  const [round, setRound] = useState(0);
  const [finished, setFinished] = useState(false);

  return (
    <div className="app">
      <p className="preview-banner" role="status">{SETUP_PREVIEW_BANNER}</p>
      {finished ? (
        <section className="card wizard-card">
          <h1>انتهت المعاينة</h1>
          <p>لم يتم حفظ أو تنفيذ أي تغيير.</p>
          <button className="primary-button" type="button" onClick={() => { setFinished(false); setRound((current) => current + 1); }}>
            معاينة من جديد
          </button>
        </section>
      ) : (
        <PreviewRound key={round} onFinished={() => setFinished(true)} />
      )}
    </div>
  );
}

function PreviewRound({ onFinished }: { onFinished: () => void }) {
  const [value, setValue] = useState<BranchSetupInput>(emptyBranchSetup);
  return (
    <SetupWizard
      preview
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={onFinished}
    />
  );
}
