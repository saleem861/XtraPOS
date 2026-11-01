import { OperationProgress } from "./OperationProgress";
import { ScreenHeader } from "./ScreenHeader";

interface StatusScreenProps {
  title: string;
  message: string;
  actionLabel?: string;
  onAction?: () => void;
}

export function StatusScreen({ title, message, actionLabel, onAction }: StatusScreenProps) {
  return (
    <section className="card">
      <ScreenHeader title={title} />
      {message.startsWith("جاري") ? <OperationProgress label={message} /> : (
        <header className="card-header">
          <p>{message}</p>
        </header>
      )}
      {actionLabel && onAction ? (
        <div className="form">
          <button className="primary-button" type="button" onClick={onAction}>
            {actionLabel}
          </button>
        </div>
      ) : null}
    </section>
  );
}
