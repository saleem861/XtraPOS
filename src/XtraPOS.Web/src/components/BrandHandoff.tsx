import { useEffect, useRef } from "react";
import { claimLocalDeviceRegistration, getSetupStatus } from "../api/client";
import { loadPersistedDevice } from "../storage/deviceConfig";
import type { DeviceSession } from "../types/setup";

export const setupHandoffKey = "xtrapos.brandHandoff";

export function markSetupHandoff() {
  sessionStorage.setItem(setupHandoffKey, "1");
}

export function hasSetupHandoff() {
  return sessionStorage.getItem(setupHandoffKey) === "1";
}

export function clearSetupHandoff() {
  sessionStorage.removeItem(setupHandoffKey);
}

export function BrandHandoff({ onReady, onGiveUp }: { onReady: (session: DeviceSession) => void; onGiveUp: () => void }) {
  const readyRef = useRef(onReady);
  const giveUpRef = useRef(onGiveUp);
  readyRef.current = onReady;
  giveUpRef.current = onGiveUp;

  useEffect(() => {
    let cancelled = false;
    const started = Date.now();
    let timer = 0;
    const tick = async () => {
      if (cancelled) return;
      let session: DeviceSession | null = null;
      try {
        const status = await getSetupStatus();
        if (status.status === "Configured" || status.status === "ConfiguredButAdminMissing") {
          session = await claimLocalDeviceRegistration();
          if (!session) {
            const stored = await loadPersistedDevice();
            if (stored.kind === "ready") session = stored.session;
          }
        }
      } catch {
        session = null;
      }
      if (cancelled) return;
      if (session && Date.now() - started >= 1200) {
        clearSetupHandoff();
        readyRef.current(session);
        return;
      }
      if (Date.now() - started >= 45000) {
        clearSetupHandoff();
        giveUpRef.current();
        return;
      }
      timer = window.setTimeout(() => void tick(), 700);
    };
    timer = window.setTimeout(() => void tick(), 300);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, []);

  return (
    <section className="brand-handoff" role="status" aria-live="polite" aria-label="جاري فتح إكسترا بوس">
      <p className="brand-handoff-ar">إكسترا بوس</p>
      <p className="brand-handoff-en">XtraPOS</p>
    </section>
  );
}
