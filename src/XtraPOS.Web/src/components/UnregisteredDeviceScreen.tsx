import { useEffect, useState, type FormEvent } from "react";
import {
  createTestBranch,
  getLocalDeviceRecovery,
  listBranchDatabases,
  recoverMainDevice,
  SetupClientError,
  type BranchDatabaseCandidate,
  type LocalRecoveryContext,
} from "../api/client";
import type { DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

interface UnregisteredDeviceScreenProps {
  onRecovered: (session: DeviceSession) => void;
  onCancel?: () => void;
  intent?: "join" | "independent" | "central";
  onboardingAuthorization?: string;
}

type Mode = "choice" | "join" | "joinForm" | "create";

export function UnregisteredDeviceScreen({ onRecovered, onCancel, intent = "join", onboardingAuthorization = "" }: UnregisteredDeviceScreenProps) {
  const [mode, setMode] = useState<Mode>(intent === "join" ? "join" : "create");
  const linkToCentral = intent === "central";
  const [branches, setBranches] = useState<BranchDatabaseCandidate[]>([]);
  const [selected, setSelected] = useState<BranchDatabaseCandidate | null>(null);
  const [context, setContext] = useState<LocalRecoveryContext | null>(null);
  const [loadError, setLoadError] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [deviceName, setDeviceName] = useState("");
  const [branchName, setBranchName] = useState("");
  const [branchCode, setBranchCode] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [centralUsername, setCentralUsername] = useState("");
  const [centralPassword, setCentralPassword] = useState("");
  const [stationId, setStationId] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    if (mode !== "join") {
      return;
    }

    let active = true;
    void listBranchDatabases()
      .then((loaded) => {
        if (active) {
          setBranches(loaded);
        }
      })
      .catch((reason: unknown) => {
        if (active) {
          setLoadError(reason instanceof SetupClientError ? reason.message : "تعذر قراءة الفروع.");
        }
      });
    return () => {
      active = false;
    };
  }, [mode]);

  async function openBranch(branch: BranchDatabaseCandidate) {
    setPending(true);
    setError("");
    setSelected(branch);
    try {
      const loaded = await getLocalDeviceRecovery(branch.databaseName);
      setContext(loaded);
      setDeviceName(loaded.suggestedDeviceName);
      setStationId(loaded.stations[0]?.stationId ?? "");
      setMode("joinForm");
    } catch (reason: unknown) {
      setError(reason instanceof SetupClientError ? reason.message : "تعذر قراءة الفرع.");
    } finally {
      setPending(false);
    }
  }

  async function join(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending || !context || !selected) {
      return;
    }

    setPending(true);
    setError("");
    try {
      onRecovered(await recoverMainDevice({
        username,
        password,
        deviceName,
        stationId: stationId || undefined,
        stationNumber: context.stations.length === 0 ? 1 : undefined,
        stationName: context.stations.length === 0 ? "كاونتر 1" : undefined,
        databaseName: selected.databaseName,
      }));
    } catch (reason: unknown) {
      setError(reason instanceof SetupClientError ? reason.message : "تعذر ربط هذا الجهاز.");
      setPending(false);
    }
  }

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) {
      return;
    }

    setPending(true);
    setError("");
    try {
      onRecovered(await createTestBranch({
        adminUsername: username,
        adminPassword: password,
        adminDisplayName: displayName || username,
        branchName,
        branchCode,
        deviceName,
        linkToCentral,
        centralUsername: linkToCentral ? centralUsername : undefined,
        centralPassword: linkToCentral ? centralPassword : undefined,
        onboardingAuthorization: linkToCentral ? undefined : onboardingAuthorization,
      }));
    } catch (reason: unknown) {
      setError(reason instanceof SetupClientError ? reason.message : "تعذر إنشاء الفرع.");
      setPending(false);
    }
  }

  return (
    <section className="card login-card">
      <ScreenHeader title="إعداد XtraPOS" />
      <header className="card-header">
        <p>هذا الجهاز غير مرتبط بفرع</p>
      </header>
      {mode === "join" ? (
        <>
          {loadError ? <p className="form-error">{loadError}</p> : null}
          <div className="choice-list">
            {branches.map((branch) => (
              <button className="choice-button" key={branch.databaseName} type="button" disabled={pending} onClick={() => void openBranch(branch)}>
                <strong>{branch.branchName}</strong>
                <span>{branch.businessName}</span>
              </button>
            ))}
          </div>
          {error ? <p className="form-error">{error}</p> : null}
        </>
      ) : null}
      {mode === "joinForm" && context && selected ? (
        <form className="form" onSubmit={(event) => void join(event)}>
          <dl className="details compact-details">
            <div><dt>اسم المنشأة</dt><dd>{context.businessName}</dd></div>
            <div><dt>اسم الفرع</dt><dd>{context.branchName}</dd></div>
          </dl>
          <label className="field"><span>اسم المستخدم</span><input value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="username" disabled={pending} /></label>
          <label className="field"><span>كلمة المرور</span><input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="current-password" disabled={pending} /></label>
          {context.stations.length > 0 ? (
            <label className="field">
              <span>نقطة البيع</span>
              <select value={stationId} onChange={(event) => setStationId(event.target.value)} disabled={pending}>
                {context.stations.map((station) => (
                  <option key={station.stationId} value={station.stationId}>{station.stationName}</option>
                ))}
              </select>
            </label>
          ) : null}
          <label className="field"><span>اسم الجهاز</span><input value={deviceName} onChange={(event) => setDeviceName(event.target.value)} disabled={pending} /></label>
          {error ? <p className="form-error">{error}</p> : null}
          <button className="primary-button" type="submit" disabled={pending || deviceName.trim().length === 0}>{pending ? "جارٍ الربط" : "ربط هذا الجهاز"}</button>
          <button className="ghost-button" type="button" onClick={() => setMode("join")}>رجوع</button>
        </form>
      ) : null}
      {mode === "create" ? (
        <form className="form" onSubmit={(event) => void create(event)}>
          <p>{linkToCentral
            ? "يُنشأ فرع جديد ويُربط بالإدارة المركزية بعد التحقق من مدير المركز. لا تُستخدم بيانات فرع المقاطعة."
            : "يُنشأ فرع مستقل ومديره الأول على هذا الجهاز. لا تُطلب بيانات فرع المقاطعة ولا رمز إعداد."}</p>
          <label className="field"><span>اسم الفرع</span><input value={branchName} onChange={(event) => setBranchName(event.target.value)} disabled={pending} /></label>
          <label className="field"><span>رمز الفرع</span><input value={branchCode} onChange={(event) => setBranchCode(event.target.value)} disabled={pending} /></label>
          <label className="field"><span>اسم الجهاز</span><input value={deviceName} onChange={(event) => setDeviceName(event.target.value)} disabled={pending} /></label>
          {linkToCentral ? (
            <>
              <label className="field"><span>مدير الإدارة المركزية</span><input value={centralUsername} onChange={(event) => setCentralUsername(event.target.value)} autoComplete="username" disabled={pending} /></label>
              <label className="field"><span>كلمة مرور المركز</span><input type="password" value={centralPassword} onChange={(event) => setCentralPassword(event.target.value)} autoComplete="current-password" disabled={pending} /></label>
            </>
          ) : null}
          <label className="field"><span>مدير الفرع الجديد</span><input value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="off" disabled={pending} /></label>
          <label className="field"><span>الاسم الظاهر</span><input value={displayName} onChange={(event) => setDisplayName(event.target.value)} disabled={pending} /></label>
          <label className="field"><span>كلمة مرور المدير الجديد</span><input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="new-password" disabled={pending} /></label>
          {error ? <p className="form-error">{error}</p> : null}
          <button className="primary-button" type="submit" disabled={pending}>إنشاء الفرع وربط الجهاز</button>
          <button className="ghost-button" type="button" onClick={() => onCancel?.()}>رجوع</button>
        </form>
      ) : null}
    </section>
  );
}
