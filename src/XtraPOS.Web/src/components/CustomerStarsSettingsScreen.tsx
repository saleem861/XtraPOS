import { useEffect, useMemo, useState } from "react";
import { createCustomerPurchasesClient, starGlyphs, type StarThresholds } from "../api/customerPurchases";
import type { DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import "./customerPurchases.css";

const LEVELS = [
  { key: "oneStar", label: "نجمة واحدة" },
  { key: "twoStars", label: "نجمتان" },
  { key: "threeStars", label: "3 نجوم" },
  { key: "fourStars", label: "4 نجوم" },
  { key: "fiveStars", label: "5 نجوم" },
] as const;

type LevelKey = (typeof LEVELS)[number]["key"];

/** يتحقق من الحدود كما يتحقق الخادم: مدة 7–730 يوماً، وكل حد أكبر من الذي قبله. */
export function thresholdsProblem(windowDays: number, values: Record<LevelKey, number>) {
  if (!Number.isInteger(windowDays) || windowDays < 7 || windowDays > 730) return "مدة الاحتساب بين 7 و730 يوماً.";
  let previous = 0;
  for (const level of LEVELS) {
    const value = values[level.key];
    if (!Number.isFinite(value) || value <= previous) {
      return previous === 0 ? "حد النجمة الأولى أكبر من صفر." : `حد ${level.label} يجب أن يكون أكبر من الحد الذي قبله.`;
    }
    previous = value;
  }
  return "";
}

/**
 * «نجوم الزبائن»: مجموع مشتريات الزبون خلال آخر عدد من الأيام يحدد نجومه من 1 إلى 5.
 * الحفظ يضيف صفاً جديداً (الأحدث هو المعمول به) ويُرحَّل إلى المركز ليصل إلى كل الفروع.
 */
export function CustomerStarsSettingsScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const client = useMemo(() => createCustomerPurchasesClient(session), [session]);
  const [current, setCurrent] = useState<StarThresholds | null>(null);
  const [canManage, setCanManage] = useState(false);
  const [windowDays, setWindowDays] = useState("90");
  const [values, setValues] = useState<Record<LevelKey, string>>({ oneStar: "", twoStars: "", threeStars: "", fourStars: "", fiveStars: "" });
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");

  useEffect(() => {
    let stop = false;
    void client.thresholds()
      .then((result) => {
        if (stop) return;
        setCurrent(result.thresholds);
        setCanManage(result.canManage);
        setWindowDays(String(result.thresholds.windowDays));
        setValues({
          oneStar: String(result.thresholds.oneStar),
          twoStars: String(result.thresholds.twoStars),
          threeStars: String(result.thresholds.threeStars),
          fourStars: String(result.thresholds.fourStars),
          fiveStars: String(result.thresholds.fiveStars),
        });
      })
      .catch((caught: unknown) => { if (!stop) setError(caught instanceof Error ? caught.message : "تعذر قراءة حدود النجوم."); });
    return () => { stop = true; };
  }, [client]);

  const numbers = Object.fromEntries(LEVELS.map((level) => [level.key, Number(values[level.key])])) as Record<LevelKey, number>;
  const problem = thresholdsProblem(Number(windowDays), numbers);

  async function save() {
    if (problem) {
      setError(problem);
      return;
    }
    setPending(true);
    setError("");
    setMessage("");
    try {
      const result = await client.saveThresholds({ windowDays: Number(windowDays), ...numbers });
      setMessage(result.message);
      const fresh = await client.thresholds();
      setCurrent(fresh.thresholds);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر حفظ حدود النجوم.");
    } finally {
      setPending(false);
    }
  }

  return (
    <section className="card settings-screen customer-stars-settings" style={{ position: "relative" }}>
      <ScreenHeader title="نجوم الزبائن" onBack={onBack} />
      {!current && !error ? <Waiting label="جاري قراءة حدود النجوم…" /> : null}
      {current ? (
        <form className="form" onSubmit={(event) => { event.preventDefault(); void save(); }}>
          <p className="device-settings-hint">
            يُحسب مجموع مشتريات الزبون في كل الفروع خلال آخر عدد من الأيام، ويُقارن بالحدود أدناه. وتُقارن الفترة بالفترة التي قبلها لمعرفة تقدم الزبون أو تأخره.
          </p>
          {current.isDefault ? <p className="device-settings-hint">لم تُحدَّد حدود بعد؛ تُستعمل القيم الافتراضية المعروضة.</p> : (
            <p className="device-settings-hint">آخر تعديل: {current.updatedByName ?? "—"} {current.updatedAt ? new Date(current.updatedAt).toLocaleString("ar-PS-u-nu-latn") : ""}</p>
          )}
          <label>مدة الاحتساب (يوم)
            <input type="number" min={7} max={730} value={windowDays} disabled={!canManage || pending} onChange={(event) => setWindowDays(event.target.value)} />
          </label>
          {LEVELS.map((level, index) => (
            <label key={level.key}>
              <span><b className="customer-stars-settings-glyphs">{starGlyphs(index + 1)}</b> {level.label} من مبلغ</span>
              <input type="number" min={0} step="0.01" inputMode="decimal" value={values[level.key]} disabled={!canManage || pending}
                onChange={(event) => setValues((previous) => ({ ...previous, [level.key]: event.target.value }))} />
            </label>
          ))}
          {canManage && problem ? <p className="form-error">{problem}</p> : null}
          {error ? <p className="form-error">{error}</p> : null}
          {message ? <p className="update-result is-ok" role="status">{message}</p> : null}
          {canManage ? (
            <button className="primary-button" type="submit" disabled={pending || Boolean(problem)}>حفظ الحدود</button>
          ) : <p className="device-settings-hint">تعديل الحدود لمن يدير الزبائن.</p>}
        </form>
      ) : null}
      {error && !current ? <p className="form-error">{error}</p> : null}
      {pending ? <Waiting variant="overlay" label="جاري الحفظ…" /> : null}
    </section>
  );
}
