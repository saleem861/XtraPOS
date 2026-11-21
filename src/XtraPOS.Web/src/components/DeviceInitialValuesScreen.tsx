import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ALL_POINTS,
  createDeviceSettingsClient,
  CUSTOMER_DISPLAY_ENABLED,
  pointLabel,
  settingValue,
  type DeviceSettingValue,
  type PointSettings,
} from "../api/deviceSettings";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

function formatTime(value: string | null) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return new Intl.DateTimeFormat("ar", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function describeChange(setting: DeviceSettingValue | undefined) {
  if (!setting || setting.isDefault) return "القيمة الافتراضية، لم تُعدَّل بعد.";
  const where = setting.source === "Central" ? "وصل من الإدارة المركزية" : "عُدّل على فرع";
  const who = setting.updatedByName ? ` بواسطة ${setting.updatedByName}` : "";
  const when = formatTime(setting.updatedAtUtc);
  return `آخر تعديل: ${where}${who}${when ? ` · ${when}` : ""}`;
}

function syncLine(point: PointSettings) {
  if (point.centralVersion > 0 && point.syncedAtUtc) {
    return `مطابق لجدول الإدارة المركزية (الإصدار ${point.centralVersion}) · ${formatTime(point.syncedAtUtc)}`;
  }
  return "لم تصل نسخة هذه النقطة من الإدارة المركزية بعد.";
}

/** القيم الأولية: تفتح على النقطة الحالية، ويختار المدير أي نقطة في أي فرع أو جميع النقاط. */
export function DeviceInitialValuesScreen({ session, user, onBack }: {
  session: DeviceSession;
  user: CurrentUser;
  onBack: () => void;
}) {
  const client = useMemo(() => createDeviceSettingsClient(session), [session]);
  const [points, setPoints] = useState<PointSettings[]>([]);
  const [canManageAll, setCanManageAll] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [selected, setSelected] = useState<string>("");
  const [enabled, setEnabled] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [pending, setPending] = useState(true);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const point = selected === ALL_POINTS ? null : points.find((item) => item.deviceId === selected) ?? null;

  const load = useCallback(async (keep?: string) => {
    setPending(true);
    setError("");
    try {
      const list = await client.points();
      setPoints(list.points);
      setCanManageAll(list.canManageAll);
      setNotice(list.notice);
      const next = keep && (keep === ALL_POINTS || list.points.some((item) => item.deviceId === keep))
        ? keep
        : list.points.find((item) => item.isCurrent)?.deviceId ?? list.points[0]?.deviceId ?? "";
      setSelected(next);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر تحميل نقاط البيع.");
    } finally {
      setPending(false);
    }
  }, [client]);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    setDirty(false);
    if (selected === ALL_POINTS) {
      setEnabled(points.length > 0 && points.every((item) => settingValue(item.settings, CUSTOMER_DISPLAY_ENABLED)));
    } else {
      setEnabled(settingValue(point?.settings, CUSTOMER_DISPLAY_ENABLED));
    }
  }, [selected, points, point]);

  async function save() {
    if (!selected) return;
    setPending(true);
    setError("");
    setMessage("");
    try {
      const target = selected === ALL_POINTS ? ALL_POINTS : point;
      if (!target) throw new Error("اختر نقطة.");
      const result = await client.save(target, { [CUSTOMER_DISPLAY_ENABLED]: enabled });
      setMessage(result.message);
      await load(selected);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر حفظ القيم الأولية.");
    } finally {
      setPending(false);
    }
  }

  const setting = point?.settings.find((item) => item.key === CUSTOMER_DISPLAY_ENABLED);
  return (
    <section className="card device-settings">
      <ScreenHeader title="القيم الأولية" context={`${user.branchName} · ${user.deviceName}`} onBack={onBack} />

      <label className="device-point-picker">
        <span>النقطة</span>
        <select
          value={selected}
          disabled={pending || !canManageAll}
          onChange={(event) => { setMessage(""); setSelected(event.target.value); }}
        >
          {points.map((item) => (
            <option key={item.deviceId} value={item.deviceId}>
              {pointLabel(item)}{item.isCurrent ? " — النقطة الحالية" : ""}
            </option>
          ))}
          {canManageAll ? <option value={ALL_POINTS}>جميع النقاط في كل الفروع</option> : null}
        </select>
      </label>
      {notice ? <p className="device-settings-hint">{notice}</p> : null}
      <p className="device-settings-hint">
        {selected === ALL_POINTS
          ? "سيُطبَّق الإعداد على كل نقاط البيع في كل الفروع."
          : point?.isCurrent
            ? "هذه النقطة التي تعمل عليها الآن. التعديل يخصها وحدها."
            : "التعديل يخص النقطة المختارة وحدها، ويصل إلى فرعها بعد ترحيله إلى الإدارة المركزية."}
      </p>

      <div className="device-setting-row">
        <label className="check-line">
          <input
            type="checkbox"
            checked={enabled}
            disabled={pending || !selected}
            onChange={(event) => { setEnabled(event.target.checked); setDirty(true); setMessage(""); }}
          />
          <span>تفعيل شاشة الزبون</span>
        </label>
        {point ? <small>{describeChange(setting)}</small> : null}
        {point ? <small>{syncLine(point)}</small> : null}
      </div>

      {error ? <p className="form-error">{error}</p> : null}
      {message ? <p className="update-result is-ok" role="status">{message}</p> : null}
      <button className="primary-button" type="button" disabled={pending || !dirty} onClick={() => void save()}>
        {selected === ALL_POINTS ? "حفظ لجميع النقاط" : "حفظ"}
      </button>

    </section>
  );
}
