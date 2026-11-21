import { useCallback, useEffect, useMemo, useState } from "react";
import {
  createDeviceSettingsClient,
  CUSTOMER_DISPLAY_ENABLED,
  settingValue,
  type CentralDeviceSettings,
  type CurrentDeviceSettings,
  type DeviceSettingsAuditEntry,
  type DeviceSettingsClient,
  type DeviceSettingsHistory,
  type DeviceSettingsSyncState,
  type DeviceSettingValue,
  type TransferStatus,
} from "../api/deviceSettings";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

const syncText: Record<DeviceSettingsSyncState, string> = {
  LocalOnly: "محفوظ على هذا الفرع فقط (لا توجد إدارة مركزية مرتبطة).",
  PendingCentral: "محفوظ على الفرع، وبانتظار الترحيل إلى الإدارة المركزية. سيُعاد الترحيل تلقائياً.",
  ReconciliationRequired: "محفوظ على الفرع، لكن أحد الترحيلات يحتاج مراجعة. راجع سجل الترحيلات أدناه.",
  Synced: "محفوظ على الفرع ومرحَّل إلى الإدارة المركزية.",
};

const transferStatusText: Record<TransferStatus, string> = {
  Pending: "بانتظار الترحيل",
  Completed: "مُرحَّل",
  ReconciliationRequired: "يحتاج مراجعة",
};

function formatTime(value: string | null) {
  if (!value) {
    return "";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "";
  }
  return new Intl.DateTimeFormat("ar", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function settingLabel(key: string) {
  return key === CUSTOMER_DISPLAY_ENABLED ? "شاشة الزبون" : key;
}

function valueText(value: string | null) {
  if (value === "true") return "مفعّل";
  if (value === "false") return "غير مفعّل";
  return value ?? "—";
}

function describeChange(setting: DeviceSettingValue | undefined) {
  if (!setting || setting.isDefault) {
    return "القيمة الافتراضية، لم تُعدَّل بعد.";
  }
  const where = setting.source === "Central" ? "من الإدارة المركزية" : "من الفرع";
  const who = setting.updatedByName ? ` بواسطة ${setting.updatedByName}` : "";
  const when = formatTime(setting.updatedAtUtc);
  return `آخر تعديل ${where}${who}${when ? ` · ${when}` : ""}`;
}

function CustomerDisplayToggle({
  settings,
  checked,
  disabled,
  onChange,
}: {
  settings: DeviceSettingValue[];
  checked: boolean;
  disabled: boolean;
  onChange: (value: boolean) => void;
}) {
  const setting = settings.find((item) => item.key === CUSTOMER_DISPLAY_ENABLED);
  return (
    <div className="device-setting-row">
      <label className="check-line">
        <input
          type="checkbox"
          checked={checked}
          disabled={disabled}
          onChange={(event) => onChange(event.target.checked)}
        />
        <span>{setting?.label ?? "تفعيل شاشة الزبون"}</span>
      </label>
      <small>{describeChange(setting)}</small>
    </div>
  );
}

/** سجل الترحيلات من هذه النقطة إلى المركز، وما وصل من المركز إليها. */
function TransferLog({ history }: { history: DeviceSettingsHistory | null }) {
  if (!history) {
    return null;
  }

  return (
    <section className="device-transfer-log" aria-label="سجل الترحيلات">
      <h2>سجل الترحيلات</h2>
      {history.transfers.length === 0 ? (
        <p className="device-settings-hint">لا توجد ترحيلات من هذه النقطة بعد.</p>
      ) : (
        <table className="device-log-table">
          <thead>
            <tr>
              <th>وقت الحفظ</th>
              <th>الإعداد</th>
              <th>بواسطة</th>
              <th>الحالة</th>
              <th>المحاولات</th>
              <th>التفاصيل</th>
            </tr>
          </thead>
          <tbody>
            {history.transfers.map((transfer) => (
              <tr key={transfer.outboxEventId} className={`device-transfer-${transfer.status}`}>
                <td>{formatTime(transfer.createdAt)}</td>
                <td>{transfer.changedKeys.map(settingLabel).join("، ")}</td>
                <td>{transfer.createdByName ?? "—"}</td>
                <td>{transferStatusText[transfer.status]}</td>
                <td>{transfer.attemptCount}</td>
                <td>
                  {transfer.status === "Completed"
                    ? `رُحِّل ${formatTime(transfer.completedAt)}${transfer.centralVersion ? ` · إصدار ${transfer.centralVersion}` : ""}`
                    : [
                        transfer.lastError,
                        transfer.diagnosticId ? `رمز التشخيص: ${transfer.diagnosticId}` : null,
                        transfer.nextRetryAt ? `المحاولة التالية: ${formatTime(transfer.nextRetryAt)}` : null,
                      ]
                        .filter(Boolean)
                        .join(" · ") || "لم تتم محاولة بعد"}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {history.received.length > 0 ? (
        <>
          <h3>وصل من الإدارة المركزية</h3>
          <ul className="device-received-list">
            {history.received.map((item) => (
              <li key={item.changeSequence}>
                {formatTime(item.appliedAt)} · إصدار {item.centralVersion} · {item.outcome === "Applied" ? "طُبِّق على هذه النقطة" : "لا تغيير"}
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </section>
  );
}

interface DeviceInitialValuesScreenProps {
  session: DeviceSession;
  user: CurrentUser;
  onBack: () => void;
  client?: DeviceSettingsClient;
}

/** القيم الأولية للنقطة الحالية: للمدير والمشرف، وتخص هذه النقطة فقط. */
export function DeviceInitialValuesScreen({ session, user, onBack, client }: DeviceInitialValuesScreenProps) {
  const api = useMemo(() => client ?? createDeviceSettingsClient(session), [client, session]);
  const [data, setData] = useState<CurrentDeviceSettings | null>(null);
  const [history, setHistory] = useState<DeviceSettingsHistory | null>(null);
  const [customerDisplay, setCustomerDisplay] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [pending, setPending] = useState(true);

  const loadHistory = useCallback(async () => {
    try {
      setHistory(await api.history());
    } catch {
      setHistory(null);
    }
  }, [api]);

  useEffect(() => {
    let active = true;
    api
      .current()
      .then((result) => {
        if (!active) return;
        setData(result);
        setCustomerDisplay(settingValue(result.settings, CUSTOMER_DISPLAY_ENABLED));
        void loadHistory();
      })
      .catch((reason: unknown) => {
        if (active) setError(reason instanceof Error ? reason.message : "تعذر قراءة القيم الأولية.");
      })
      .finally(() => {
        if (active) setPending(false);
      });
    return () => {
      active = false;
    };
  }, [api, loadHistory]);

  const dirty = data !== null && settingValue(data.settings, CUSTOMER_DISPLAY_ENABLED) !== customerDisplay;

  async function save() {
    setPending(true);
    setError("");
    setNotice("");
    try {
      const result = await api.saveCurrent({ [CUSTOMER_DISPLAY_ENABLED]: customerDisplay });
      setData(result);
      setCustomerDisplay(settingValue(result.settings, CUSTOMER_DISPLAY_ENABLED));
      setNotice("تم حفظ القيم الأولية لهذه النقطة.");
      await loadHistory();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "تعذر حفظ القيم الأولية.");
    } finally {
      setPending(false);
    }
  }

  return (
    <section className="card device-settings">
      <ScreenHeader title="القيم الأولية للنقطة الحالية" context={`${user.branchName} · ${user.deviceName}`} onBack={onBack} />
      <dl className="home-facts">
        <div>
          <dt>الفرع</dt>
          <dd>{data?.branchName ?? user.branchName}</dd>
        </div>
        <div>
          <dt>النقطة</dt>
          <dd>{data?.deviceName ?? user.deviceName}{data?.posNumber ? ` (رقم ${data.posNumber})` : ""}</dd>
        </div>
      </dl>
      <p className="device-settings-hint">هذه الإعدادات تخص هذه النقطة فقط، ولا تؤثر على باقي نقاط الفرع.</p>
      <form
        className="form"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <CustomerDisplayToggle
          settings={data?.settings ?? []}
          checked={customerDisplay}
          disabled={pending || data === null}
          onChange={(value) => {
            setNotice("");
            setCustomerDisplay(value);
          }}
        />
        {data ? (
          <p className={`device-sync-state device-sync-${data.syncState}`} role="status">
            {syncText[data.syncState]}
            {data.pendingTransfers > 0 ? ` (${data.pendingTransfers} غير مكتمل)` : ""}
            {data.lastError ? ` · آخر خطأ: ${data.lastError}` : ""}
            {data.diagnosticId ? ` · رمز التشخيص: ${data.diagnosticId}` : ""}
          </p>
        ) : null}
        {error ? <p className="form-error">{error}</p> : null}
        {notice ? <p className="new-customer-success">{notice}</p> : null}
        <button className="primary-button" type="submit" disabled={pending || !dirty}>
          حفظ
        </button>
      </form>
      <TransferLog history={history} />
    </section>
  );
}

interface CentralDeviceSettingsScreenProps {
  session: DeviceSession;
  onBack: () => void;
  client?: DeviceSettingsClient;
}

/** سجل التدقيق المركزي لنقطة: كل قيمة تغيّرت، قبل وبعد، ومن، ومن أين. */
function AuditLog({ entries }: { entries: DeviceSettingsAuditEntry[] | null }) {
  if (!entries) {
    return null;
  }

  return (
    <section className="device-transfer-log" aria-label="سجل تعديلات النقطة">
      <h2>سجل تعديلات النقطة</h2>
      {entries.length === 0 ? (
        <p className="device-settings-hint">لا توجد تعديلات مسجّلة لهذه النقطة بعد.</p>
      ) : (
        <table className="device-log-table">
          <thead>
            <tr>
              <th>الوقت</th>
              <th>الإعداد</th>
              <th>قبل</th>
              <th>بعد</th>
              <th>المصدر</th>
              <th>بواسطة</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((entry, index) => (
              <tr key={`${entry.occurredAt}-${entry.settingKey}-${index}`}>
                <td>{formatTime(entry.occurredAt)}</td>
                <td>{entry.label ?? entry.settingKey}</td>
                <td>{valueText(entry.beforeValue)}</td>
                <td>{valueText(entry.afterValue)}</td>
                <td>{entry.source === "Central" ? "الإدارة المركزية" : "الفرع (ترحيل)"}</td>
                <td>{entry.actorName ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

/** لموظف الإدارة المركزية: اختيار أي نقطة في أي فرع وتعديل قيمها الأولية. */
export function CentralDeviceSettingsScreen({ session, onBack, client }: CentralDeviceSettingsScreenProps) {
  const api = useMemo(() => client ?? createDeviceSettingsClient(session), [client, session]);
  const [devices, setDevices] = useState<CentralDeviceSettings[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [customerDisplay, setCustomerDisplay] = useState(false);
  const [audit, setAudit] = useState<DeviceSettingsAuditEntry[] | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [pending, setPending] = useState(true);

  const loadAudit = useCallback(
    async (deviceId: string) => {
      try {
        setAudit(await api.centralHistory(deviceId));
      } catch {
        setAudit(null);
      }
    },
    [api],
  );

  useEffect(() => {
    let active = true;
    api
      .listCentral()
      .then((result) => {
        if (!active) return;
        setDevices(result);
        const first = result.at(0);
        if (first) {
          setSelectedId(first.deviceId);
          setCustomerDisplay(settingValue(first.settings, CUSTOMER_DISPLAY_ENABLED));
          void loadAudit(first.deviceId);
        }
      })
      .catch((reason: unknown) => {
        if (active) setError(reason instanceof Error ? reason.message : "تعذر تحميل نقاط البيع.");
      })
      .finally(() => {
        if (active) setPending(false);
      });
    return () => {
      active = false;
    };
  }, [api, loadAudit]);

  const selected = devices.find((device) => device.deviceId === selectedId) ?? null;
  const dirty = selected !== null && settingValue(selected.settings, CUSTOMER_DISPLAY_ENABLED) !== customerDisplay;

  function label(device: CentralDeviceSettings) {
    const branch = device.branchName ?? "فرع غير معروف";
    const name = device.deviceName ?? "نقطة بلا اسم";
    return `${branch} · ${name}${device.posNumber ? ` (رقم ${device.posNumber})` : ""}`;
  }

  function select(deviceId: string) {
    setSelectedId(deviceId);
    setNotice("");
    setError("");
    setAudit(null);
    const device = devices.find((item) => item.deviceId === deviceId);
    setCustomerDisplay(device ? settingValue(device.settings, CUSTOMER_DISPLAY_ENABLED) : false);
    void loadAudit(deviceId);
  }

  async function save() {
    if (!selected) return;
    setPending(true);
    setError("");
    setNotice("");
    try {
      const result = await api.saveCentral(selected.deviceId, { [CUSTOMER_DISPLAY_ENABLED]: customerDisplay });
      setDevices((current) => current.map((device) => (device.deviceId === result.deviceId ? result : device)));
      setCustomerDisplay(settingValue(result.settings, CUSTOMER_DISPLAY_ENABLED));
      setNotice("تم الحفظ في الإدارة المركزية. يصل التعديل إلى الفرع خلال دقيقة تقريباً.");
      await loadAudit(result.deviceId);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "تعذر حفظ إعدادات النقطة.");
    } finally {
      setPending(false);
    }
  }

  return (
    <section className="card device-settings">
      <ScreenHeader title="إعدادات نقاط البيع في الفروع" context="الإدارة المركزية" onBack={onBack} />
      {!pending && devices.length === 0 && !error ? (
        <p className="device-settings-hint">لا توجد نقاط بيع مسجّلة في الإدارة المركزية بعد.</p>
      ) : null}
      {devices.length > 0 ? (
        <form
          className="form"
          onSubmit={(event) => {
            event.preventDefault();
            void save();
          }}
        >
          <label>
            نقطة البيع
            <select aria-label="نقطة البيع" value={selectedId} disabled={pending} onChange={(event) => select(event.target.value)}>
              {devices.map((device) => (
                <option key={device.deviceId} value={device.deviceId}>
                  {label(device)}
                </option>
              ))}
            </select>
          </label>
          <CustomerDisplayToggle
            settings={selected?.settings ?? []}
            checked={customerDisplay}
            disabled={pending || selected === null}
            onChange={(value) => {
              setNotice("");
              setCustomerDisplay(value);
            }}
          />
          {error ? <p className="form-error">{error}</p> : null}
          {notice ? <p className="new-customer-success">{notice}</p> : null}
          <button className="primary-button" type="submit" disabled={pending || !dirty}>
            حفظ للنقطة المختارة
          </button>
        </form>
      ) : error ? (
        <p className="form-error">{error}</p>
      ) : null}
      <AuditLog entries={selected ? audit : null} />
    </section>
  );
}
