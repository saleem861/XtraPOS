import { useEffect, useState, useSyncExternalStore } from "react";
import { installWaitingActivity, subscribeWaiting, waitingSnapshot } from "./waitingActivity";

const SHOW_AFTER_MS = 350;

let cached = waitingSnapshot();
function read() {
  const next = waitingSnapshot();
  if (next.userRequests !== cached.userRequests || next.localWaits !== cached.localWaits) cached = next;
  return cached;
}

// مؤشر عائم يظهر عند انتظار طلب بدأه المستخدم أكثر من لحظة، ما لم تعرض الشاشة مؤشرها الخاص.
export function GlobalWaiting() {
  useEffect(() => installWaitingActivity(), []);
  const { userRequests, localWaits } = useSyncExternalStore(subscribeWaiting, read, read);
  const busy = userRequests > 0 && localWaits === 0;
  const [shown, setShown] = useState(false);
  useEffect(() => {
    if (!busy) {
      setShown(false);
      return undefined;
    }
    const timer = window.setTimeout(() => setShown(true), SHOW_AFTER_MS);
    return () => window.clearTimeout(timer);
  }, [busy]);
  if (!shown) return null;
  return (
    <div className="xp-wait-floating" role="status" aria-live="polite">
      <span className="xp-wait-ring" aria-hidden="true"><span /><span /><span /></span>
      <span className="xp-wait-label">يرجى الانتظار…</span>
    </div>
  );
}
