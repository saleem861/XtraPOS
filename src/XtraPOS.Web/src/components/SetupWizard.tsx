import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { ScreenHeader } from "./ScreenHeader";
import { BRANCH_TYPES, BRANCH_TYPE_LABELS, emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import {
  canAdvance,
  executeLabel,
  isFinalStep,
  isCentralLinkedNewBranch,
  isNewBranchDraft,
  isWizardRouteDeterministic,
  stepCounterLabel,
  visibleSteps,
  withNewBranchDeviceDraft,
  withNewBranchStationDraft,
  type CentralDatabaseChoice,
  type WizardBranchChoice,
  type WizardStepId,
} from "../setup/wizardFlow";
import {
  BRANCH_CODE_DUPLICATE_MESSAGE,
  BRANCH_CODE_HELPER,
  PREVIEW_BRANCH_CODES,
  branchCodeRequest,
  isDuplicateBranchCode,
  suggestBranchCode,
} from "../setup/branchCode";
import { listCentralAdmins, verifyCentralAdmin, type CentralAdminChoice } from "../setup/centralAdmins";
import { readHostDeviceLabel, suggestDeviceName } from "../setup/deviceName";
import { OFFLINE_ACKNOWLEDGEMENT, OFFLINE_CACHE_STORE, OFFLINE_STOCK_NOTICE } from "../setup/offlineContract";
import { PREVIEW_MANY_BRANCHES, PREVIEW_ONE_BRANCH, PREVIEW_LOCAL_CENTRALS, PREVIEW_MANY_LOCAL_CENTRALS, PREVIEW_ONE_LOCAL_BRANCH, PREVIEW_MANY_LOCAL_BRANCHES, PREVIEW_STATIONS, previewTopology } from "../setup/setupPreview";
import { mainBranchReview, secondaryDeviceReview, type ReviewSection } from "../setup/wizardReview";
import { acceptedLocalBranchDatabases, type BranchDatabaseChoice } from "../setup/branchDiscovery";
import {
  branchDatabaseNameReady,
  clearedBranchIdentity,
  connectionInputsReady,
  displayedServer,
  PREVIEW_TAKEN_BRANCH_DATABASES,
  suggestNewBranchDatabaseName,
  withConnectionVerified,
} from "../setup/connectionContext";
import {
  createSecondaryStation,
  listExistingBranchStations,
  listSecondaryStations,
  verifySecondaryPairing,
  type SecondaryStation,
} from "../setup/secondaryDevice";
import { SetupClientError } from "../api/client";

interface SetupWizardProps {
  value: BranchSetupInput;
  onChange: (value: BranchSetupInput) => void;
  onCancel: () => void;
  onSubmit: (value: BranchSetupInput) => void;
  preview?: boolean;
  initialIndex?: number;
  minimumIndex?: number;
  testConnection?: (value: BranchSetupInput, target: "central" | "branch" | "remote" | "secondary") => Promise<{
    ok: boolean;
    message?: string;
    businessName?: string;
    branches?: WizardBranchChoice[];
  }>;
  discoverLocalCentrals?: () => Promise<CentralDatabaseChoice[]>;
  discoverLocalBranches?: (businessName: string) => Promise<BranchDatabaseChoice[]>;
}

const STEP_TITLES: Record<WizardStepId, string> = {
  deviceRole: "ما الذي تريد إعداد هذا الجهاز ليكون؟",
  branchMode: "هل يتبع إدارة مركزية؟",
  centralIntent: "إعداد الإدارة المركزية",
  centralPlace: "أين تريد إنشاء القاعدة المركزية؟",
  centralConnection: "اتصال قاعدة البيانات",
  centralSelect: "اختيار القاعدة المركزية",
  centralLinked: "تم الاتصال بالإدارة المركزية",
  centralBusiness: "هوية المنشأة",
  centralReview: "مراجعة الإدارة المركزية",
  branchIntent: "كيف تريد إعداد قاعدة بيانات هذا الفرع؟",
  branchPlace: "أين توجد قاعدة بيانات هذا الفرع؟",
  branchDiscover: "اختيار قاعدة بيانات الفرع الموجودة",
  branchConnection: "اتصال قاعدة الفرع",
  businessIdentity: "هوية المنشأة",
  branchIdentity: "هوية هذا الفرع",
  remoteCopy: "هل تريد الوصول إلى بيانات هذا الفرع من خارج الفرع؟",
  remoteConnection: "النسخة البعيدة للفرع",
  secondaryConnect: "الاتصال بخادم الفرع",
  branchSelect: "اختيار الفرع",
  deviceAuthorization: "السماح بربط هذا الجهاز",
  posStation: "اختيار نقطة البيع",
  deviceIdentity: "تعريف هذا الجهاز",
  offlinePrep: "تجهيز العمل دون اتصال",
  secondaryReview: "مراجعة ربط الجهاز",
  deviceName: "اسم الجهاز",
  admin: "حساب المدير",
  execute: "مراجعة الإعداد",
};

function stepHeading(step: WizardStepId, value: BranchSetupInput): string {
  if (step === "centralPlace" && value.centralIntent === "existing") {
    return "أين توجد القاعدة المركزية الموجودة؟";
  }
  if (step === "branchPlace" && value.branchIntent === "new") {
    return "أين تريد إنشاء قاعدة بيانات هذا الفرع؟";
  }
  if (step === "branchPlace") {
    return "أين توجد قاعدة بيانات هذا الفرع؟";
  }
  if (step === "branchConnection" && value.branchIntent === "new" && value.branchLocation === "Local") {
    return "قاعدة بيانات الفرع المحلية";
  }
  if (step === "posStation" && isNewBranchDraft(value)) {
    return "نقطة البيع";
  }
  if (step === "admin" && isCentralLinkedNewBranch(value)) {
    return "التحقق من حساب المدير";
  }
  return STEP_TITLES[step];
}

function adminLabel(admin: CentralAdminChoice): string {
  const employee = admin.employeeNumber?.trim() ?? "";
  if (employee && employee !== admin.username) {
    return `${admin.username} / ${employee}`;
  }
  return admin.username;
}

function applyCentralAdmin(draft: BranchSetupInput, admin: CentralAdminChoice): BranchSetupInput {
  return {
    ...draft,
    centralAdminUserId: admin.userId,
    centralAdminRole: admin.roleName,
    username: admin.username,
    displayName: admin.displayName,
    fullName: admin.fullName?.trim() || admin.displayName,
    employeeNumber: admin.employeeNumber?.trim() || admin.username,
    password: "",
    confirmPassword: "",
  };
}

export function SetupWizard({ value, onChange, onCancel, onSubmit, testConnection, discoverLocalCentrals, discoverLocalBranches, preview = false, initialIndex = 0, minimumIndex = 0 }: SetupWizardProps) {
  const [index, setIndex] = useState(initialIndex);
  const [branches, setBranches] = useState<WizardBranchChoice[]>([]);
  const [centralChoices, setCentralChoices] = useState<CentralDatabaseChoice[]>([]);
  const [localBranches, setLocalBranches] = useState<BranchDatabaseChoice[]>([]);
  const [branchLookup, setBranchLookup] = useState<"pending" | "done">("pending");
  const [remoteStations, setRemoteStations] = useState<SecondaryStation[]>([]);
  const [centralAdmins, setCentralAdmins] = useState<CentralAdminChoice[]>([]);
  const [summary, setSummary] = useState<SecondaryConnectionSummary | null>(null);
  const [message, setMessage] = useState("");
  const [pending, setPending] = useState(false);
  const [previewCentralScenario, setPreviewCentralScenario] = useState<"" | "one" | "many" | "none">("");
  const steps = visibleSteps(value, branches, centralChoices);
  const step = steps[Math.min(index, steps.length - 1)] ?? "deviceRole";
  const ready = canAdvance(value, step, branches, centralChoices);
  const finishing = (step === "centralLinked" && value.deviceRole === "CentralOnly") || isFinalStep(step);

  function update(next: Partial<BranchSetupInput>) {
    onChange({ ...value, ...next });
    setMessage("");
  }

  async function verifyCentralAdminPassword(draft: BranchSetupInput) {
    if (preview) {
      const match = centralAdmins.find((admin) => admin.username === draft.username.trim()) ?? centralAdmins[0];
      if (!match || draft.password.length === 0) {
        setMessage("أدخل اسم المستخدم وكلمة المرور.");
        return;
      }
      onChange(applyCentralAdmin({ ...draft, password: draft.password }, match));
      setMessage("تم التحقق من حساب المدير.");
      return;
    }

    setPending(true);
    try {
      const admin = await verifyCentralAdmin(draft);
      onChange(applyCentralAdmin(draft, admin));
      setMessage("تم التحقق من حساب المدير.");
    } catch (error) {
      setMessage(error instanceof SetupClientError ? error.message : "تعذر التحقق من حساب المدير.");
    } finally {
      setPending(false);
    }
  }

  useEffect(() => {
    if (!isStagingHost() || value.centralDatabaseName === defaultCentralDatabaseName()) {
      return;
    }
    onChange({
      ...value,
      centralDatabaseName: defaultCentralDatabaseName(),
      centralUsername: "",
      centralPassword: "",
      centralConnectionTested: false,
    });
  }, []);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!ready || pending) {
      return;
    }
    if (step === "branchIdentity") {
      const problem = preview
        ? previewBranchCodeProblem(value)
        : await branchCodeProblem(value);
      if (problem) {
        setMessage(problem);
        return;
      }
    }
    if (finishing) {
      onSubmit(value);
      return;
    }

    let draft = value;
    if (step === "centralPlace" && value.centralIntent === "existing" && value.centralLocation === "Local") {
      if (preview && previewCentralScenario === "") {
        setMessage("اختر سيناريو المعاينة قبل المتابعة.");
        return;
      }
      if (preview && (previewCentralScenario === "none" || centralChoices.length === 0)) {
        setMessage("لا توجد قاعدة مركزية متوافقة على هذا الجهاز.");
        return;
      }
      setPending(true);
      try {
        const found = preview ? centralChoices : await (discoverLocalCentrals ?? discoverCompatibleCentrals)();
        setCentralChoices(found);
        if (found.length === 0) {
          setMessage("لا توجد قاعدة مركزية متوافقة على هذا الجهاز.");
          return;
        }
        draft = found.length === 1
          ? applyCentralChoice(value, found[0])
          : { ...value, centralDatabaseName: "", businessName: "", centralServer: "localhost", centralConnectionTested: false, centralPassword: "" };
        onChange(draft);
      } catch (error) {
        setMessage(error instanceof Error ? error.message : "تعذر البحث عن القواعد المركزية.");
        return;
      } finally {
        setPending(false);
      }
    }
    if (step === "branchPlace" && value.branchIntent === "existing" && value.branchLocation === "Local" && !preview) {
      setPending(true);
      try {
        const business = value.operatingMode === "CentralManagement" ? value.businessName.trim() : "";
        const found = acceptedLocalBranchDatabases(
          await (discoverLocalBranches ?? discoverCompatibleBranches)(business),
          business,
        );
        setLocalBranches(found);
        setBranchLookup("done");
        draft = found.length === 1
          ? applyBranchDatabase(value, found[0])
          : { ...value, branchDatabaseName: "", branchConnectionTested: false };
        onChange(draft);
      } catch (error) {
        setMessage(error instanceof Error ? error.message : "تعذر البحث عن قواعد الفرع.");
        return;
      } finally {
        setPending(false);
      }
    }
    if (step === "posStation" && !preview && value.deviceRole === "Secondary" && !draft.stationId.trim()) {
      setPending(true);
      try {
        const created = await createSecondaryStation(draft);
        draft = {
          ...draft,
          stationId: created.stationId,
          stationNumber: String(created.stationNumber),
          stationName: created.stationName,
        };
        onChange(draft);
      } catch (error) {
        setMessage(error instanceof SetupClientError ? error.message : "تعذر إنشاء نقطة البيع.");
        return;
      } finally {
        setPending(false);
      }
    }

    const nextIndex = Math.min(index + 1, steps.length - 1);
    const nextStep = steps[nextIndex];
    if (nextStep === "branchConnection" && draft.branchIntent === "new" && draft.branchLocation === "Local" && !draft.branchDatabaseName.trim()) {
      const code = preview ? suggestBranchCode(PREVIEW_BRANCH_CODES) : "";
      draft = { ...draft, branchDatabaseName: suggestNewBranchDatabaseName(code) };
      onChange(draft);
    }
    if (nextStep === "branchIdentity" && !draft.branchCode.trim()) {
      const suggested = preview
        ? suggestBranchCode(PREVIEW_BRANCH_CODES)
        : await suggestedBranchCode(draft);
      if (suggested) {
        draft = { ...draft, branchCode: suggested };
        onChange(draft);
      }
    }
    if (nextStep === "posStation" && isNewBranchDraft(draft)) {
      draft = withNewBranchStationDraft(draft);
      onChange(draft);
    }
    if (nextStep === "deviceName" && isNewBranchDraft(draft)) {
      draft = withNewBranchDeviceDraft(draft);
      onChange(draft);
    }
    if (nextStep === "deviceIdentity" && !draft.deviceName.trim()) {
      const suggested = suggestDeviceName(readHostDeviceLabel());
      if (suggested) {
        draft = { ...draft, deviceName: suggested };
        onChange(draft);
      }
    }
    setMessage("");
    setIndex(nextIndex);
    if (nextStep === "admin" && isCentralLinkedNewBranch(draft)) {
      if (preview) {
        const previewAdmin: CentralAdminChoice = {
          userId: "00000000-0000-0000-0000-000000000001",
          username: "preview.admin",
          displayName: "مدير المعاينة",
          fullName: "مدير المعاينة",
          employeeNumber: "preview.admin",
          roleCode: "Admin",
          roleName: "مدير النظام",
        };
        setCentralAdmins([previewAdmin]);
        onChange(applyCentralAdmin(draft, previewAdmin));
      } else {
        setPending(true);
        try {
          const admins = await listCentralAdmins(draft);
          setCentralAdmins(admins);
          onChange({ ...draft, centralAdminUserId: "", password: "", confirmPassword: "" });
        } catch (error) {
          setCentralAdmins([]);
          setMessage(error instanceof SetupClientError ? error.message : "تعذر قراءة مديري الإدارة المركزية.");
        } finally {
          setPending(false);
        }
      }
    }
    if (!preview && nextStep === "posStation" && !isNewBranchDraft(draft)) {
      setPending(true);
      try {
        const stations = draft.deviceRole === "Secondary"
          ? await listSecondaryStations(draft)
          : await listExistingBranchStations(draft);
        setRemoteStations(stations);
      } catch (error) {
        setRemoteStations([]);
        setMessage(error instanceof SetupClientError ? error.message : "تعذر قراءة نقاط البيع.");
      } finally {
        setPending(false);
      }
    }
  }

  async function acceptPairing() {
    if (!value.pairingCode.trim() || pending) {
      return;
    }
    if (preview) {
      onChange({ ...value, pairingVerified: true, pairingToken: "preview-pairing" });
      setMessage("تم التحقق من رمز الربط. لم يُرسل أي طلب.");
      return;
    }

    setPending(true);
    try {
      const token = await verifySecondaryPairing(value);
      onChange({ ...value, pairingVerified: true, pairingToken: token });
      setMessage("تم التحقق من رمز الربط.");
    } catch (error) {
      onChange({ ...value, pairingVerified: false, pairingToken: "" });
      setMessage(error instanceof SetupClientError ? error.message : "رمز الربط غير صالح أو انتهت صلاحيته.");
    } finally {
      setPending(false);
    }
  }

  async function runTest(target: "central" | "branch" | "remote" | "secondary", credentials?: { username: string; password: string }) {
    const probe = testConnection ?? ((current) => defaultConnectionTest(current, target, credentials));
    setPending(true);
    setMessage("");
    try {
      const result = await probe(value, target);
      if (!result.ok) {
        if (target === "secondary") {
          setSummary(null);
        }
        setMessage(result.message || "فشل اختبار الاتصال.");
        return;
      }
      if (target === "central") {
        const sqlLogin = sqlCentralLogin(value, credentials);
        onChange(withConnectionVerified(value, "central", {
          centralUsername: sqlLogin ? credentials?.username ?? value.centralUsername : "",
          centralPassword: sqlLogin ? credentials?.password ?? "" : "",
          ...(value.centralIntent === "existing" && result.businessName ? { businessName: result.businessName } : {}),
        }));
      } else if (target === "branch") {
        const sqlLogin = value.branchAuthentication === "Sql";
        onChange(withConnectionVerified(value, "branch", {
          branchUsername: sqlLogin ? credentials?.username ?? "" : "",
          branchPassword: sqlLogin ? credentials?.password ?? "" : "",
        }));
      } else if (target === "remote") {
        const sqlLogin = value.remoteAuthentication === "Sql";
        onChange(withConnectionVerified(value, "remote", {
          remoteUsername: sqlLogin ? credentials?.username ?? "" : "",
          remotePassword: sqlLogin ? credentials?.password ?? "" : "",
        }));
      } else {
        const found = result.branches ?? [];
        setBranches(found);
        setSummary(describeSecondaryConnection(value, found));
        update({
          secondaryTested: true,
          selectedBranchId: found.length === 1 ? found[0].branchId : "",
        });
      }
      setMessage(target === "secondary" ? "" : "تم اختبار الاتصال.");
    } finally {
      setPending(false);
    }
  }

  function simulateConnection(target: "central" | "branch" | "remote", credentials?: { username: string; password: string }) {
    const patch: Partial<BranchSetupInput> = target === "central"
      ? { centralUsername: credentials?.username ?? value.centralUsername, centralPassword: credentials?.password ?? value.centralPassword }
      : target === "branch"
        ? { branchUsername: credentials?.username ?? value.branchUsername, branchPassword: credentials?.password ?? value.branchPassword }
        : { remoteUsername: credentials?.username ?? value.remoteUsername, remotePassword: credentials?.password ?? value.remotePassword };
    const next = { ...value, ...patch };
    const ready = target === "central"
      ? connectionInputsReady({
        remote: next.centralLocation === "Remote",
        host: next.centralServer,
        databaseName: next.centralDatabaseName,
        authentication: next.centralAuthentication,
        username: next.centralUsername,
        password: next.centralPassword,
      })
      : target === "branch"
        ? next.branchIntent === "new" && next.branchLocation === "Local"
          ? branchDatabaseNameReady(next.branchDatabaseName, preview ? PREVIEW_TAKEN_BRANCH_DATABASES : [])
          : connectionInputsReady({
            remote: next.branchLocation === "Remote",
            host: next.branchServer,
            databaseName: next.branchDatabaseName,
            authentication: next.branchAuthentication,
            username: next.branchUsername,
            password: next.branchPassword,
            rejectCentralName: true,
          })
        : connectionInputsReady({
          remote: true,
          host: next.remoteServer,
          databaseName: next.branchRemoteDatabaseName,
          authentication: next.remoteAuthentication,
          username: next.remoteUsername,
          password: next.remotePassword,
          rejectCentralName: true,
        });
    if (!ready) {
      setMessage("أكمل بيانات الاتصال المطلوبة قبل المحاكاة.");
      return;
    }
    onChange(withConnectionVerified(next, target, target === "central" && next.centralIntent === "existing"
      ? { businessName: next.businessName.trim() || "منشأة المعاينة" }
      : {}));
    setMessage("تمت محاكاة نجاح الاتصال. لم يُرسل أي طلب.");
  }

  function simulateBranches(many: boolean) {
    const found = many ? PREVIEW_MANY_BRANCHES : PREVIEW_ONE_BRANCH;
    setBranches(found);
    setSummary(describeSecondaryConnection(value, found));
    update({
      secondaryTested: true,
      selectedBranchId: found.length === 1 ? found[0].branchId : "",
    });
    setMessage("تمت محاكاة نجاح الاتصال. لم يُرسل أي طلب.");
  }

  function revealLocalBranches(choices: BranchDatabaseChoice[]) {
    const business = value.operatingMode === "CentralManagement" ? value.businessName.trim() : "";
    const accepted = acceptedLocalBranchDatabases(choices, business);
    setLocalBranches(accepted);
    setBranchLookup("done");
    if (accepted.length === 1) {
      update(applyBranchDatabase(value, accepted[0]));
      return;
    }
    update({ branchDatabaseName: "", branchConnectionTested: false });
  }

  function chooseCentralScenario(kind: "one" | "many" | "none") {
    setPreviewCentralScenario(kind);
    if (kind === "one") {
      setCentralChoices(PREVIEW_LOCAL_CENTRALS);
      onChange(applyCentralChoice(value, PREVIEW_LOCAL_CENTRALS[0]));
      setMessage("");
      return;
    }
    if (kind === "many") {
      setCentralChoices(PREVIEW_MANY_LOCAL_CENTRALS);
      onChange({
        ...value,
        centralDatabaseName: "",
        businessName: "",
        centralServer: "localhost",
        centralConnectionTested: false,
        centralVerifiedKey: "",
        centralPassword: "",
      });
      setMessage("");
      return;
    }
    setCentralChoices([]);
    onChange({
      ...value,
      centralDatabaseName: "",
      businessName: "",
      centralConnectionTested: false,
      centralVerifiedKey: "",
      centralPassword: "",
    });
    setMessage("لا توجد قاعدة مركزية متوافقة على هذا الجهاز.");
  }

  const displayedIndex = Math.min(index, steps.length - 1);

  return (
    <section className="card wizard-card">
      <ScreenHeader title="إعداد XtraPOS" />
      <header className="card-header">
        <p>{stepCounterLabel(displayedIndex, steps.length, isWizardRouteDeterministic(value, centralChoices, localBranches.length, branchLookup === "done"))}</p>
      </header>
      <form className="form wizard-form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <div className="wizard-body">
        <h2>{stepHeading(step, value)}</h2>
        {step === "deviceRole" ? (
          <ChoiceGroup
            name="deviceRole"
            value={value.deviceRole}
            onChange={(deviceRole) => {
              const nextRole = deviceRole as BranchSetupInput["deviceRole"];
              if (nextRole === value.deviceRole) {
                return;
              }
              setBranches([]);
              setCentralChoices([]);
              setLocalBranches([]);
              setBranchLookup("pending");
              setSummary(null);
              setRemoteStations([]);
              setPreviewCentralScenario("");
              setMessage("");
              onChange({ ...emptyBranchSetup, deviceRole: nextRole });
            }}
            options={[
              ["MainBranch", "خادم / جهاز رئيسي لفرع", "يدير قاعدة بيانات الفرع، ويمكن ربطه بإدارة مركزية ونسخة بعيدة."],
              ["Secondary", "جهاز إضافي داخل فرع موجود", "كاشير أو جهاز إدارة إضافي يتصل بخادم الفرع، ولا ينشئ قاعدة SQL مستقلة."],
              ["CentralOnly", "خادم إدارة مركزية فقط", "لإنشاء أو استخدام قاعدة XtraPOS_Central بدون أن يكون الجهاز فرع بيع."],
            ]}
          />
        ) : null}
        {step === "branchMode" ? (
          <ChoiceGroup
            name="branchMode"
            value={value.operatingMode}
            onChange={(operatingMode) => {
              setCentralChoices([]);
              setPreviewCentralScenario("");
              update({
                operatingMode: operatingMode as BranchSetupInput["operatingMode"],
                linkToCentral: operatingMode === "CentralManagement" ? "yes" : "no",
                centralIntent: "",
                centralLocation: "",
                centralConnectionTested: false,
                centralVerifiedKey: "",
                businessName: "",
              });
            }}
            options={[
              ["Standalone", "لا", "لا يتبع إدارة مركزية. تُنشأ بيانات الفرع ومديره على هذا الجهاز."],
              ["CentralManagement", "نعم", "يتبع إدارة مركزية. تُزامن حسابات المستخدمين أولًا ثم يُتحقق من مدير مركزي."],
            ]}
          />
        ) : null}
        {step === "centralIntent" ? (
          <ChoiceGroup
            name="centralIntent"
            value={value.centralIntent}
            onChange={(centralIntent) => {
              setCentralChoices([]);
              setPreviewCentralScenario("");
              const nextIntent = centralIntent as BranchSetupInput["centralIntent"];
              update({
                centralIntent: nextIntent,
                centralAction: nextIntent === "new" ? "create" : "existing",
                centralConnectionTested: false,
                centralVerifiedKey: "",
                businessName: "",
                centralDatabaseName: nextIntent === "new" ? defaultCentralDatabaseName() : "",
                centralPassword: "",
              });
            }}
            options={[
              ["new", "إنشاء قاعدة مركزية جديدة", ""],
              ["existing", "استخدام قاعدة مركزية موجودة", ""],
            ]}
          />
        ) : null}
        {step === "centralPlace" ? (
          <>
          <ChoiceGroup
            name="centralPlace"
            value={value.centralLocation}
            onChange={(centralLocation) => {
              setCentralChoices([]);
              setPreviewCentralScenario("");
              update({
                centralLocation: centralLocation as BranchSetupInput["centralLocation"],
                centralConnectionTested: false,
                centralVerifiedKey: "",
                ...(value.centralIntent === "existing" ? { centralPassword: "", businessName: "", centralDatabaseName: "" } : {}),
              });
            }}
            options={[
              ["Local", "على هذا الجهاز", ""],
              ["Remote", "على جهاز / خادم آخر", ""],
            ]}
          />
          {import.meta.env.DEV && preview && value.centralIntent === "existing" && value.centralLocation === "Local" ? (
            <PreviewTools>
              <button className="ghost-button" type="button" onClick={() => chooseCentralScenario("one")}>قاعدة واحدة</button>
              <button className="ghost-button" type="button" onClick={() => chooseCentralScenario("many")}>عدة قواعد</button>
              <button className="ghost-button" type="button" onClick={() => chooseCentralScenario("none")}>لا توجد قاعدة متوافقة</button>
            </PreviewTools>
          ) : null}
          </>
        ) : null}
        {step === "centralConnection" && value.centralIntent === "existing" ? (
          <ExistingCentralConnection
            value={value}
            preview={preview}
            pending={pending}
            onChange={(next) => update(next)}
            onTest={(credentials) => void runTest("central", credentials)}
            onSimulate={(credentials) => simulateConnection("central", credentials)}
          />
        ) : null}
        {step === "centralConnection" && value.centralIntent !== "existing" ? (
          <>
            <p>القاعدة المركزية هي مرجع المنشأة والفروع. ليست النسخة البعيدة للفرع.</p>
            <ConnectionFields
              server={value.centralServer}
              port={value.centralPort}
              instance={value.centralInstance}
              databaseName={isStagingHost() ? defaultCentralDatabaseName() : value.centralDatabaseName}
              lockDatabase={isStagingHost()}
              remote={value.centralLocation === "Remote"}
              preview={preview}
              encrypt={value.centralEncrypt}
              trustServerCertificate={value.centralTrustServerCertificate}
              authentication={value.centralAuthentication === "Sql" ? "Sql" : "Integrated"}
              onServer={(centralServer) => update({ centralServer, centralConnectionTested: false })}
              onPort={(centralPort) => update({ centralPort, centralConnectionTested: false })}
              onInstance={(centralInstance) => update({ centralInstance, centralConnectionTested: false })}
              onDatabase={(centralDatabaseName) => update({ centralDatabaseName, centralConnectionTested: false })}
              onTransport={(transport) => update({ ...transport, centralConnectionTested: false })}
              onAuthentication={(centralAuthentication) => update({
                centralAuthentication,
                centralConnectionTested: false,
                ...(centralAuthentication === "Integrated" ? { centralUsername: "", centralPassword: "" } : {}),
              })}
              onTest={(credentials) => void runTest("central", credentials)}
              onSimulate={(credentials) => simulateConnection("central", credentials)}
              pending={pending}
            />
          </>
        ) : null}
        {step === "centralSelect" ? (
          <div className="choice-list">
            {centralChoices.map((item) => (
              <button
                className={value.centralDatabaseName === item.databaseName ? "type-option is-selected" : "type-option"}
                key={item.databaseName}
                type="button"
                onClick={() => update(applyCentralChoice(value, item))}
              >
                <strong>{item.businessName}</strong>
                <span>اسم قاعدة البيانات: {item.databaseName}</span>
                <span>الخادم: {item.server}</span>
              </button>
            ))}
          </div>
        ) : null}
        {step === "centralLinked" ? (
          <ul className="review-list">
            <li>اسم المنشأة: {value.businessName}</li>
            <li>اسم قاعدة البيانات: {value.centralDatabaseName}</li>
            <li>الخادم: {displayedServer(value.centralLocation, value.centralServer)}</li>
          </ul>
        ) : null}
        {step === "centralBusiness" || step === "businessIdentity" ? (
          <>
            <label className="field">
              <span>اسم المنشأة</span>
              <input value={value.businessName} onChange={(event) => update({ businessName: event.target.value })} />
            </label>
            <label className="field">
              <span>الاسم المختصر (اختياري)</span>
              <input value={value.businessDisplayName} onChange={(event) => update({ businessDisplayName: event.target.value })} />
            </label>
            <label className="field">
              <span>الوصف (اختياري)</span>
              <textarea value={value.address} onChange={(event) => update({ address: event.target.value })} rows={3} />
            </label>
          </>
        ) : null}
        {step === "centralReview" ? (
          <>
            <ul className="review-list">
              <li>نوع الإعداد: إدارة مركزية</li>
              <li>موقع القاعدة: {value.centralLocation === "Remote" ? "بعيد" : "على هذا الجهاز"}</li>
              <li>الخادم: {displayedServer(value.centralLocation, value.centralServer)}</li>
              {value.centralLocation === "Remote" && value.centralPort.trim() ? <li>المنفذ: {value.centralPort.trim()}</li> : null}
              {value.centralInstance.trim() ? <li>اسم الـ Instance: {value.centralInstance.trim()}</li> : null}
              <li>اسم قاعدة البيانات: {value.centralDatabaseName}</li>
              <li>اسم المنشأة: {value.centralIntent === "existing" ? "من القاعدة المركزية" : value.businessName}</li>
            </ul>
            <p>هذه البيانات ستكون المرجع الأساسي للفروع والأجهزة المرتبطة.</p>
          </>
        ) : null}
        {step === "branchIntent" ? (
          <ChoiceGroup
            name="branchIntent"
            value={value.branchIntent}
            onChange={(branchIntent) => {
              setLocalBranches([]);
              setBranchLookup("pending");
              const nextIntent = branchIntent as BranchSetupInput["branchIntent"];
              update(nextIntent === "new"
                ? { ...clearedBranchIdentity(), branchIntent: nextIntent }
                : {
                  ...clearedBranchIdentity(),
                  branchIntent: nextIntent,
                });
            }}
            options={[
              ["new", "إنشاء قاعدة بيانات جديدة", ""],
              ["existing", "استخدام قاعدة بيانات موجودة", ""],
            ]}
          />
        ) : null}
        {step === "branchPlace" ? (
          <ChoiceGroup
            name="branchPlace"
            value={value.branchLocation}
            onChange={(branchLocation) => {
              setLocalBranches([]);
              setBranchLookup("pending");
              update({
                branchLocation: branchLocation as BranchSetupInput["branchLocation"],
                branchConnectionTested: false,
                branchVerifiedKey: "",
                branchManualConnection: false,
                branchDatabaseName: "",
                branchServer: "",
                branchPort: "",
                branchInstance: "",
                ...(value.branchIntent === "existing"
                  ? { branchName: "", branchCode: "", branchType: "" as const, attachedBranchId: "", attachedBranchCode: "" }
                  : {}),
              });
            }}
            options={[
              ["Local", "على هذا الجهاز", ""],
              ["Remote", "على جهاز / خادم آخر", ""],
            ]}
          />
        ) : null}
        {step === "branchDiscover" ? (
          <LocalBranchLookup
            preview={preview}
            lookup={branchLookup}
            choices={localBranches}
            selectedName={value.branchDatabaseName}
            branchName={value.branchName}
            onReveal={(choices) => revealLocalBranches(choices)}
            onSelect={(choice) => update(applyBranchDatabase(value, choice))}
            onManual={() => {
              update({ branchManualConnection: true, branchDatabaseName: "", branchConnectionTested: false });
              setIndex((current) => current + 1);
            }}
          />
        ) : null}
        {step === "branchConnection" && value.branchIntent === "new" && value.branchLocation === "Local" ? (
          <NewLocalBranchDatabase
            databaseName={value.branchDatabaseName}
            preview={preview}
            pending={pending}
            onDatabase={(branchDatabaseName) => update({ branchDatabaseName, branchConnectionTested: false, branchVerifiedKey: "" })}
            onSimulate={() => simulateConnection("branch")}
            onConfirm={() => {
              if (!branchDatabaseNameReady(value.branchDatabaseName)) {
                setMessage("اسم قاعدة البيانات غير صالح.");
                return;
              }
              onChange(withConnectionVerified(value, "branch"));
              setMessage("تم التحقق من اسم قاعدة البيانات.");
            }}
          />
        ) : null}
        {step === "branchConnection" && value.branchLocation === "Local" && !(value.branchIntent === "new") ? (
          <BranchManualConnection
            databaseName={value.branchDatabaseName}
            authentication={value.branchAuthentication === "Sql" ? "Sql" : "Integrated"}
            preview={preview}
            pending={pending}
            onDatabase={(branchDatabaseName) => update({ branchDatabaseName, branchConnectionTested: false, branchVerifiedKey: "" })}
            onAuthentication={(branchAuthentication) => update({
              branchAuthentication,
              branchConnectionTested: false,
              branchVerifiedKey: "",
              ...(branchAuthentication === "Integrated" ? { branchUsername: "", branchPassword: "" } : {}),
            })}
            onTest={(credentials) => void runTest("branch", credentials)}
            onSimulate={(credentials) => simulateConnection("branch", credentials)}
          />
        ) : null}
        {(step === "branchConnection" && value.branchLocation !== "Local") || step === "remoteConnection" ? (
          <>
            <p>
              {step === "remoteConnection"
                ? "النسخة البعيدة للفرع نسخة تشغيلية/متابعة لهذا الفرع، وليست القاعدة المركزية. الاتجاه الطبيعي: الفرع المحلي → النسخة البعيدة. الاستعادة العكسية عملية منفصلة لاستعادة الفرع."
                : "هذه قاعدة هذا الفرع. ليست القاعدة المركزية."}
            </p>
            {step === "branchConnection" && value.operatingMode === "CentralManagement" && value.centralLocation === "Remote" && value.centralServer.trim() ? (
              <label className="check-line">
                <input
                  type="checkbox"
                  checked={value.branchUseCentralServer}
                  onChange={(event) => update(event.target.checked
                    ? {
                      branchUseCentralServer: true,
                      branchServer: value.centralServer,
                      branchPort: value.centralPort,
                      branchInstance: value.centralInstance,
                      branchConnectionTested: false,
                      branchVerifiedKey: "",
                    }
                    : {
                      branchUseCentralServer: false,
                      branchServer: "",
                      branchPort: "",
                      branchInstance: "",
                      branchConnectionTested: false,
                      branchVerifiedKey: "",
                    })}
                />
                <span>استخدام نفس خادم الإدارة المركزية</span>
              </label>
            ) : null}
            <ConnectionFields
              server={step === "remoteConnection" ? value.remoteServer : value.branchServer}
              port={step === "remoteConnection" ? value.remotePort : value.branchPort}
              instance={step === "remoteConnection" ? value.remoteInstance : value.branchInstance}
              databaseName={step === "remoteConnection" ? value.branchRemoteDatabaseName : value.branchDatabaseName}
              remote
              preview={preview}
              rejectCentralName
              authentication={step === "remoteConnection"
                ? (value.remoteAuthentication === "Sql" ? "Sql" : "Integrated")
                : (value.branchAuthentication === "Sql" ? "Sql" : "Integrated")}
              onServer={(server) => update(step === "remoteConnection"
                ? { remoteServer: server, remoteCopyTested: false, remoteVerifiedKey: "" }
                : { branchServer: server, branchUseCentralServer: false, branchConnectionTested: false, branchVerifiedKey: "" })}
              onPort={(port) => update(step === "remoteConnection"
                ? { remotePort: port, remoteCopyTested: false, remoteVerifiedKey: "" }
                : { branchPort: port, branchUseCentralServer: false, branchConnectionTested: false, branchVerifiedKey: "" })}
              onInstance={(instance) => update(step === "remoteConnection"
                ? { remoteInstance: instance, remoteCopyTested: false, remoteVerifiedKey: "" }
                : { branchInstance: instance, branchUseCentralServer: false, branchConnectionTested: false, branchVerifiedKey: "" })}
              onDatabase={(name) => update(step === "remoteConnection"
                ? { branchRemoteDatabaseName: name, remoteCopyTested: false, remoteVerifiedKey: "" }
                : { branchDatabaseName: name, branchConnectionTested: false, branchVerifiedKey: "" })}
              onAuthentication={(authentication) => update(step === "remoteConnection"
                ? {
                  remoteAuthentication: authentication,
                  remoteCopyTested: false,
                  remoteVerifiedKey: "",
                  ...(authentication === "Integrated" ? { remoteUsername: "", remotePassword: "" } : {}),
                }
                : {
                  branchAuthentication: authentication,
                  branchConnectionTested: false,
                  branchVerifiedKey: "",
                  ...(authentication === "Integrated" ? { branchUsername: "", branchPassword: "" } : {}),
                })}
              onTest={(credentials) => void runTest(step === "remoteConnection" ? "remote" : "branch", credentials)}
              onSimulate={(credentials) => simulateConnection(step === "remoteConnection" ? "remote" : "branch", credentials)}
              pending={pending}
            />
          </>
        ) : null}
        {step === "branchIdentity" ? (
          <>
            {value.operatingMode === "CentralManagement" ? <p>هوية المنشأة تُحمّل من الإدارة المركزية.</p> : null}
            <label className="field">
              <span>اسم الفرع</span>
              <input value={value.branchName} onChange={(event) => update({ branchName: event.target.value })} />
            </label>
            <label className="field">
              <span>رمز الفرع</span>
              <input className="latin-input" value={value.branchCode} onChange={(event) => update({ branchCode: event.target.value })} />
            </label>
            <p>{BRANCH_CODE_HELPER}</p>
            <ChoiceGroup
              name="branchType"
              value={value.branchType}
              onChange={(branchType) => update({ branchType: branchType as BranchSetupInput["branchType"] })}
              options={BRANCH_TYPES.map((type) => [type, BRANCH_TYPE_LABELS[type], ""])}
            />
          </>
        ) : null}
        {step === "remoteCopy" ? (
          <>
            <p>النسخة البعيدة للفرع نسخة تشغيلية/متابعة لهذا الفرع، وليست القاعدة المركزية.</p>
            <p>الاتجاه الطبيعي: الفرع المحلي → النسخة البعيدة. الاستعادة العكسية عملية منفصلة لاستعادة الفرع.</p>
            <ChoiceGroup
              name="remoteCopy"
              value={value.branchRemoteChoice}
              onChange={(branchRemoteChoice) => update({
                branchRemoteChoice: branchRemoteChoice as BranchSetupInput["branchRemoteChoice"],
                remoteCopyTested: false,
                remoteVerifiedKey: "",
              })}
              options={[
                ["yes", "نعم، أريد نسخة بعيدة متزامنة", "الاتجاه الطبيعي: الفرع المحلي → النسخة البعيدة. الاستعادة العكسية عملية منفصلة لاستعادة الفرع."],
                ["no", "لا، سأستخدم الفرع محليًا فقط", ""],
              ]}
            />
          </>
        ) : null}
        {step === "secondaryConnect" ? (
          <>
            <ChoiceGroup
              name="secondaryMode"
              value={value.secondaryMode}
              onChange={(secondaryMode) => {
                setSummary(null);
                setBranches([]);
                update({
                  secondaryMode: secondaryMode as BranchSetupInput["secondaryMode"],
                  secondaryTested: false,
                  selectedBranchId: "",
                  pairingVerified: false,
                  pairingToken: "",
                  pairingCode: "",
                });
              }}
              options={[
                ["discover", "البحث تلقائيًا على الشبكة المحلية", ""],
                ["manual", "إدخال عنوان الخادم يدويًا", ""],
              ]}
            />
            {value.secondaryMode === "discover" ? (
              <>
                <button className="ghost-button" type="button" disabled={pending} onClick={() => void runTest("secondary")}>البحث الآن</button>
                {import.meta.env.DEV && preview ? (
                  <PreviewTools>
                    <button className="ghost-button" type="button" onClick={() => simulateBranches(false)}>خادم يعيد فرعًا واحدًا</button>
                    <button className="ghost-button" type="button" onClick={() => simulateBranches(true)}>خادم يعيد عدة فروع</button>
                  </PreviewTools>
                ) : null}
              </>
            ) : null}
            {value.secondaryMode === "manual" ? (
              <>
                <label className="field">
                  <span>الخادم / عنوان IP</span>
                  <input className="latin-input" value={value.secondaryHost} onChange={(event) => { setSummary(null); setBranches([]); update({ secondaryHost: event.target.value, secondaryTested: false, selectedBranchId: "", pairingVerified: false, pairingToken: "", pairingCode: "" }); }} />
                </label>
                <label className="field">
                  <span>المنفذ</span>
                  <input className="latin-input" inputMode="numeric" value={value.secondaryPort} onChange={(event) => { setSummary(null); update({ secondaryPort: event.target.value, secondaryTested: false }); }} />
                </label>
                {import.meta.env.DEV && preview ? (
                  <PreviewTools>
                    <button className="ghost-button" type="button" onClick={() => simulateBranches(false)}>خادم يعيد فرعًا واحدًا</button>
                    <button className="ghost-button" type="button" onClick={() => simulateBranches(true)}>خادم يعيد عدة فروع</button>
                  </PreviewTools>
                ) : (
                  <button className="ghost-button" type="button" disabled={pending} onClick={() => void runTest("secondary")}>اختبار الاتصال</button>
                )}
              </>
            ) : null}
            {summary && value.secondaryTested ? <SecondarySummary summary={summary} /> : null}
          </>
        ) : null}
        {step === "branchSelect" ? (
          <div className="choice-list">
            {branches.map((branch) => (
              <button
                className={value.selectedBranchId === branch.branchId ? "type-option is-selected" : "type-option"}
                key={branch.branchId}
                type="button"
                onClick={() => update({ selectedBranchId: branch.branchId })}
              >
                <strong>{branch.branchName}</strong>
                {branch.branchCode ? <span>رمز الفرع: {branch.branchCode}</span> : null}
                <span>اسم قاعدة البيانات: {branch.databaseName}</span>
              </button>
            ))}
          </div>
        ) : null}
        {step === "deviceAuthorization" ? (
          <>
            <p>يتم إنشاء رمز الربط من خادم الفرع بواسطة مستخدم مخول. معرفة عنوان الخادم وحدها لا تسمح بربط جهاز جديد.</p>
            <p>الرمز مؤقت ويُستخدم مرة واحدة.</p>
            <label className="field">
              <span>رمز ربط الجهاز</span>
              <input
                className="latin-input"
                value={value.pairingCode}
                onChange={(event) => update({ pairingCode: event.target.value, pairingVerified: false, pairingToken: "" })}
              />
            </label>
            <button
              className="ghost-button"
              type="button"
              disabled={pending || value.pairingCode.trim().length === 0}
              onClick={() => void acceptPairing()}
            >
              التحقق من الرمز
            </button>
          </>
        ) : null}
        {step === "posStation" ? (
          <>
            {preview ? (
              <ChoiceGroup
                name="previewStation"
                value={PREVIEW_STATIONS.some((station) => station.name === value.stationName) ? value.stationName : "new"}
                onChange={(stationName) => {
                  const station = PREVIEW_STATIONS.find((item) => item.name === stationName);
                  if (!station) {
                    update({ stationId: "", stationNumber: "", stationName: "" });
                    return;
                  }
                  update({ stationId: "", stationNumber: station.number, stationName: station.name });
                }}
                options={[
                  ...PREVIEW_STATIONS.map((station) => [station.name, station.name, ""] as [string, string, string]),
                  ["new", "إنشاء نقطة بيع جديدة", "لن يتم إنشاء نقطة بيع فعلية"],
                ]}
              />
            ) : remoteStations.length > 0 ? (
              <ChoiceGroup
                name="remoteStation"
                value={remoteStations.some((station) => station.stationId === value.stationId) ? value.stationId : "new"}
                onChange={(stationId) => {
                  const station = remoteStations.find((item) => item.stationId === stationId);
                  if (!station) {
                    update({ stationId: "", stationNumber: "", stationName: "" });
                    return;
                  }
                  update({
                    stationId: station.stationId,
                    stationNumber: String(station.stationNumber),
                    stationName: station.stationName,
                  });
                }}
                options={[
                  ...remoteStations.map((station) => [station.stationId, station.stationName, ""] as [string, string, string]),
                  ["new", "إنشاء نقطة بيع جديدة", "يتطلب صلاحية إدارة النظام على خادم الفرع."],
                ]}
              />
            ) : null}
            {(preview
              ? !PREVIEW_STATIONS.some((station) => station.name === value.stationName)
              : !remoteStations.some((station) => station.stationId === value.stationId)) ? (
              <>
                <label className="field">
                  <span>رقم نقطة البيع</span>
                  <input value={value.stationNumber} onChange={(event) => update({ stationNumber: event.target.value, stationId: "" })} />
                </label>
                <label className="field">
                  <span>اسم نقطة البيع</span>
                  <input value={value.stationName} onChange={(event) => update({ stationName: event.target.value, stationId: "" })} />
                </label>
              </>
            ) : null}
          </>
        ) : null}
        {step === "deviceIdentity" ? (
          <>
            <label className="field">
              <span>اسم الجهاز</span>
              <input value={value.deviceName} onChange={(event) => update({ deviceName: event.target.value })} />
            </label>
            <p>اسم يساعدك على تمييز هذا الجهاز داخل الفرع.</p>
          </>
        ) : null}
        {step === "offlinePrep" ? (
          <>
            <p>يحفظ الجهاز الإضافي في {OFFLINE_CACHE_STORE} فقط ما يحتاجه لمواصلة البيع:</p>
            <ul>
              <li>الأصناف المتاحة للبيع في هذا الفرع</li>
              <li>الأسعار اللازمة للبيع</li>
              <li>وحدات الصنف وطريقة تحويل المخزون</li>
              <li>الزبائن لاحقًا، عندما يوجد سجل الزبائن</li>
              <li>مبيعات معلّقة PendingSales بهوية فريدة</li>
            </ul>
            <p>{OFFLINE_STOCK_NOTICE}</p>
            {preview ? <p>لن يتم تنزيل أو حفظ بيانات في وضع المعاينة.</p> : null}
            <label className="check-line">
              <input type="checkbox" checked={value.offlineAcknowledged} onChange={(event) => update({ offlineAcknowledged: event.target.checked })} />
              <span>{OFFLINE_ACKNOWLEDGEMENT}</span>
            </label>
          </>
        ) : null}
        {step === "deviceName" ? (
          <label className="field">
            <span>اسم الجهاز</span>
            <input value={value.deviceName} onChange={(event) => update({ deviceName: event.target.value })} />
          </label>
        ) : null}
        {step === "admin" && isCentralLinkedNewBranch(value) ? (
          <>
            <p>تمت قراءة المديرين من المركزية ({centralAdmins.length}). لن يُنشأ مدير جديد. أدخل اسم مستخدم وكلمة مرور أي مدير مركزي فعّال.</p>
            <label className="field">
              <span>اسم المستخدم</span>
              <input value={value.username} onChange={(event) => update({ username: event.target.value, centralAdminUserId: "" })} autoComplete="username" />
            </label>
            <label className="field">
              <span>كلمة المرور</span>
              <input type="password" value={value.password} onChange={(event) => update({ password: event.target.value, centralAdminUserId: "" })} autoComplete="current-password" />
            </label>
            <button
              className="secondary-button"
              type="button"
              disabled={pending || value.username.trim().length === 0 || value.password.length === 0}
              onClick={() => void verifyCentralAdminPassword(value)}
            >
              تحقق من المدير
            </button>
            {centralAdmins.length > 1 ? (
              <div className="choice-list">
                {centralAdmins.map((admin) => (
                  <button
                    className={value.centralAdminUserId === admin.userId ? "type-option is-selected" : "type-option"}
                    key={admin.userId}
                    type="button"
                    onClick={() => update(applyCentralAdmin(value, admin))}
                  >
                    <strong>{admin.fullName?.trim() || admin.displayName}</strong>
                    <span>اسم المستخدم / رقم الموظف: {adminLabel(admin)}</span>
                    <span>الدور: {admin.roleName}</span>
                  </button>
                ))}
              </div>
            ) : value.centralAdminUserId ? (
              <dl className="review-list">
                <div><dt>الاسم</dt><dd>{value.fullName.trim() || value.displayName}</dd></div>
                <div><dt>اسم المستخدم / رقم الموظف</dt><dd>{value.employeeNumber.trim() && value.employeeNumber.trim() !== value.username.trim() ? `${value.username} / ${value.employeeNumber}` : value.username}</dd></div>
                <div><dt>الدور</dt><dd>{value.centralAdminRole}</dd></div>
              </dl>
            ) : centralAdmins.length === 0 ? (
              <p>لا يوجد مدير نظام فعال في الإدارة المركزية.</p>
            ) : null}
          </>
        ) : null}
        {step === "admin" && !isCentralLinkedNewBranch(value) ? (
          <>
            <label className="field" htmlFor="setup-admin-username">
              <span>اسم المستخدم</span>
              <input id="setup-admin-username" name="username" autoComplete="username" value={value.username} onChange={(event) => update({ username: event.target.value })} />
            </label>
            <label className="field" htmlFor="setup-admin-display-name">
              <span>الاسم الظاهر</span>
              <input id="setup-admin-display-name" name="displayName" autoComplete="name" value={value.displayName} onChange={(event) => update({ displayName: event.target.value })} />
            </label>
            <label className="field" htmlFor="setup-admin-password">
              <span>كلمة المرور</span>
              <input id="setup-admin-password" name="new-password" type="password" autoComplete="new-password" value={value.password} onChange={(event) => update({ password: event.target.value })} />
            </label>
            <label className="field" htmlFor="setup-admin-confirm">
              <span>تأكيد كلمة المرور</span>
              <input id="setup-admin-confirm" name="confirm-password" type="password" autoComplete="new-password" value={value.confirmPassword} onChange={(event) => update({ confirmPassword: event.target.value })} />
            </label>
          </>
        ) : null}
        {step === "secondaryReview" ? (
          <ReviewSections sections={[secondaryDeviceReview({
            branchName: branches.find((branch) => branch.branchId === value.selectedBranchId)?.branchName || "خادم الفرع",
            server: summary?.server ?? "خادم الفرع",
            address: summary?.address ?? (value.secondaryHost.trim() || "localhost"),
            port: summary?.port ?? (value.secondaryPort.trim() || "5080"),
            stationName: value.stationName,
            deviceName: value.deviceName,
            offlineEnabled: value.offlineAcknowledged,
            pairingVerified: value.pairingVerified,
          })]} />
        ) : null}
        {step === "execute" ? (
          <ReviewSections sections={mainBranchReview(value)} />
        ) : null}
        {isFinalStep(step) && step !== "centralReview" ? <TopologyLines lines={previewTopology(value, branches)} upward={false} /> : null}
        {message ? <p>{message}</p> : null}
        </div>
        <div className="wizard-actions">
          <button
            className="ghost-button"
            type="button"
            disabled={pending || (index <= minimumIndex && minimumIndex === 0 && !value.roleLocked)}
            onClick={() => {
              setMessage("");
              if (index <= minimumIndex) {
                onCancel();
                return;
              }
              setIndex((current) => Math.max(minimumIndex, current - 1));
            }}
          >
            السابق
          </button>
          <button className="primary-button" type="submit" disabled={!ready || pending}>
            {step === "centralLinked" && value.deviceRole === "CentralOnly"
              ? (preview ? "إنهاء المعاينة" : "استخدام القاعدة المركزية")
              : executeLabel(step, preview)}
          </button>
        </div>
      </form>
    </section>
  );
}

function ChoiceGroup({
  name,
  value,
  options,
  onChange,
}: {
  name: string;
  value: string;
  options: Array<[string, string, string]>;
  onChange: (value: string) => void;
}) {
  return (
    <fieldset className="field">
      {options.map(([id, label, description]) => (
        <label key={id} className={value === id ? "type-option is-selected" : "type-option"}>
          <input type="radio" name={name} checked={value === id} onChange={() => onChange(id)} />
          <span>
            <strong>{label}</strong>
            {description ? <small>{description}</small> : null}
          </span>
        </label>
      ))}
    </fieldset>
  );
}

function AuthenticationChoice({
  name,
  authentication,
  username,
  password,
  onAuthentication,
  onUsername,
  onPassword,
}: {
  name: string;
  authentication: "Integrated" | "Sql";
  username: string;
  password: string;
  onAuthentication: (value: "Integrated" | "Sql") => void;
  onUsername: (value: string) => void;
  onPassword: (value: string) => void;
}) {
  return (
    <>
      <fieldset className="choice-group">
        <legend>طريقة المصادقة</legend>
        <label className="choice">
          <input
            type="radio"
            name={name}
            checked={authentication !== "Sql"}
            onChange={() => onAuthentication("Integrated")}
          />
          <span><strong>مصادقة ويندوز</strong></span>
        </label>
        <label className="choice">
          <input
            type="radio"
            name={name}
            checked={authentication === "Sql"}
            onChange={() => onAuthentication("Sql")}
          />
          <span><strong>مصادقة SQL Server</strong></span>
        </label>
      </fieldset>
      {authentication === "Sql" ? (
        <div className="connection-pair connection-credentials">
          <label className="field">
            <span>اسم المستخدم</span>
            <input className="latin-input" value={username} onChange={(event) => onUsername(event.target.value)} autoComplete="off" />
          </label>
          <label className="field">
            <span>كلمة المرور</span>
            <input className="latin-input" type="password" value={password} onChange={(event) => onPassword(event.target.value)} autoComplete="new-password" />
          </label>
        </div>
      ) : null}
    </>
  );
}

function ConnectionFields({
  server,
  port = "",
  instance = "",
  databaseName,
  remote,
  pending,
  preview = false,
  encrypt,
  trustServerCertificate,
  authentication,
  rejectCentralName = false,
  lockDatabase = false,
  onServer,
  onPort,
  onInstance,
  onDatabase,
  onTransport,
  onAuthentication,
  onTest,
  onSimulate,
}: {
  server: string;
  port?: string;
  instance?: string;
  databaseName: string;
  remote: boolean;
  pending: boolean;
  preview?: boolean;
  encrypt?: boolean;
  trustServerCertificate?: boolean;
  authentication?: "Integrated" | "Sql";
  rejectCentralName?: boolean;
  lockDatabase?: boolean;
  onServer: (value: string) => void;
  onPort?: (value: string) => void;
  onInstance?: (value: string) => void;
  onDatabase: (value: string) => void;
  onTransport?: (value: { centralEncrypt: boolean; centralTrustServerCertificate: boolean }) => void;
  onAuthentication?: (value: "Integrated" | "Sql") => void;
  onTest: (credentials: { username: string; password: string }) => void;
  onSimulate?: (credentials: { username: string; password: string }) => void;
}) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [localAuthentication, setLocalAuthentication] = useState<"Integrated" | "Sql">("Integrated");
  const [localPort, setLocalPort] = useState(port);
  const [localInstance, setLocalInstance] = useState(instance);
  const portValue = onPort ? port : localPort;
  const instanceValue = onInstance ? instance : localInstance;
  const authenticationValue = authentication ?? localAuthentication;
  const sqlLogin = !lockDatabase && authenticationValue === "Sql";
  const formReady = lockDatabase || connectionInputsReady({
    remote,
    host: server,
    databaseName,
    authentication: authenticationValue,
    username,
    password,
    rejectCentralName,
  });

  function chooseAuthentication(next: "Integrated" | "Sql") {
    if (next === "Integrated") {
      setUsername("");
      setPassword("");
    }
    if (!authentication) {
      setLocalAuthentication(next);
    }
    onAuthentication?.(next);
  }

  useEffect(() => {
    if (lockDatabase) {
      onDatabase(defaultCentralDatabaseName());
    }
  }, [lockDatabase]);

  return (
    <div className="connection-fields">
      {lockDatabase ? (
        <p>سيتم الربط بقاعدة XtraPOS_Central الموجودة. لن يتم إنشاؤها أو إعادة تهيئتها، ولا تُعرض كلمة مرور.</p>
      ) : null}
      {remote && !lockDatabase ? (
        <>
          <div className="connection-pair">
            <label className="field">
              <span>الخادم / عنوان IP</span>
              <input className="latin-input" value={server} onChange={(event) => onServer(event.target.value)} />
            </label>
            <label className="field">
              <span>المنفذ</span>
              <input
                className="latin-input"
                inputMode="numeric"
                value={portValue}
                onChange={(event) => (onPort ?? setLocalPort)(event.target.value)}
              />
            </label>
          </div>
          <label className="field">
            <span>اسم الـ Instance (اختياري)</span>
            <input
              className="latin-input"
              value={instanceValue}
              onChange={(event) => (onInstance ?? setLocalInstance)(event.target.value)}
            />
          </label>
          <AuthenticationChoice
            name="connectionAuthentication"
            authentication={authenticationValue}
            username={username}
            password={password}
            onAuthentication={chooseAuthentication}
            onUsername={setUsername}
            onPassword={setPassword}
          />
          {onTransport ? (
            <>
              <label className="check-line">
                <input
                  type="checkbox"
                  checked={encrypt !== false}
                  onChange={(event) => onTransport({ centralEncrypt: event.target.checked, centralTrustServerCertificate: trustServerCertificate !== false })}
                />
                <span>Encrypt</span>
              </label>
              <label className="check-line">
                <input
                  type="checkbox"
                  checked={trustServerCertificate !== false}
                  onChange={(event) => onTransport({ centralEncrypt: encrypt !== false, centralTrustServerCertificate: event.target.checked })}
                />
                <span>TrustServerCertificate</span>
              </label>
            </>
          ) : null}
        </>
      ) : lockDatabase ? null : <p>خادم SQL المحلي: localhost</p>}
      <label className="field">
        <span>اسم قاعدة البيانات</span>
        <input className="latin-input" value={lockDatabase ? defaultCentralDatabaseName() : databaseName} readOnly={lockDatabase} onChange={(event) => onDatabase(event.target.value)} />
      </label>
      {preview ? (
        <button className="ghost-button" type="button" disabled={!formReady} onClick={() => onSimulate?.(sqlLogin ? { username, password } : { username: "", password: "" })}>محاكاة نجاح الاتصال</button>
      ) : (
        <button className="ghost-button" type="button" disabled={pending || !formReady} onClick={() => onTest(sqlLogin ? { username, password } : { username: "", password: "" })}>اختبار الاتصال</button>
      )}
    </div>
  );
}

interface SecondaryConnectionSummary {
  server: string;
  address: string;
  port: string;
}

function describeSecondaryConnection(draft: BranchSetupInput, found: WizardBranchChoice[]): SecondaryConnectionSummary {
  return {
    server: found.length === 1 ? found[0].branchName : "خادم الفرع",
    address: draft.secondaryMode === "manual" ? draft.secondaryHost.trim() : (draft.secondaryHost.trim() || "localhost"),
    port: draft.secondaryPort.trim() || "5080",
  };
}

function SecondarySummary({ summary }: { summary: SecondaryConnectionSummary }) {
  return (
    <div className="connection-summary" role="status">
      <p>✓ تم الاتصال بخادم XtraPOS</p>
      <p>الخادم: {summary.server}</p>
      <p>العنوان: {summary.address}</p>
      <p>المنفذ: {summary.port}</p>
    </div>
  );
}

function ReviewSections({ sections }: { sections: ReviewSection[] }) {
  return (
    <div className="setup-review">
      {sections.map((section) => (
        <section className="review-section" key={section.title || section.rows[0]?.label}>
          {section.title ? <h3>{section.title}</h3> : null}
          <ul className="review-list">
            {section.rows.map((row) => (
              <li key={`${row.label}:${row.value}`}>{row.label ? `${row.label}: ${row.value}` : row.value}</li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

function PreviewTools({ children }: { children: ReactNode }) {
  if (!import.meta.env.DEV) {
    return null;
  }
  return (
    <div className="preview-tools">
      <p>أدوات المعاينة</p>
      {children}
    </div>
  );
}
function TopologyLines({ lines, upward }: { lines: string[]; upward: boolean }) {
  return (
    <div className="topology-flow" aria-label="مسار الإعداد">
      {lines.map((line, index) => (
        <div key={line}>
          {index > 0 ? <div>{upward ? "↑" : "↓"}</div> : null}
          <strong>{line}</strong>
        </div>
      ))}
    </div>
  );
}

function isStagingHost() {
  return window.location.hostname === "test-pos.sharbelfashion.com";
}

function defaultCentralDatabaseName() {
  return "XtraPOS_Central";
}

function sqlCentralLogin(value: BranchSetupInput, credentials?: { username: string; password: string }) {
  return value.centralAuthentication === "Sql" || (value.centralIntent !== "existing" && Boolean(credentials?.username?.trim()));
}

function connectionLogin(
  target: "central" | "branch" | "remote" | "secondary",
  value: BranchSetupInput,
  credentials?: { username: string; password: string },
) {
  const sql = target === "central"
    ? value.centralAuthentication === "Sql"
    : target === "remote"
      ? value.remoteAuthentication === "Sql"
      : target === "branch" && value.branchAuthentication === "Sql";
  const username = sql
    ? credentials?.username?.trim()
      || (target === "central" ? value.centralUsername : target === "remote" ? value.remoteUsername : value.branchUsername).trim()
      || null
    : null;
  const password = sql
    ? credentials?.password
      || (target === "central" ? value.centralPassword : target === "remote" ? value.remotePassword : value.branchPassword)
      || null
    : null;
  return {
    authenticationType: sql ? "Sql" as const : "Integrated" as const,
    username: sql ? username : null,
    password: sql ? password : null,
  };
}

function applyCentralChoice(current: BranchSetupInput, chosen: CentralDatabaseChoice): BranchSetupInput {
  return withConnectionVerified({
    ...current,
    centralDatabaseName: chosen.databaseName,
    centralServer: chosen.server,
    businessName: chosen.businessName,
    centralPassword: "",
  }, "central");
}

async function discoverCompatibleCentrals(): Promise<CentralDatabaseChoice[]> {
  const response = await fetch("/api/setup/central-databases");
  const body = await response.json() as { message?: string; databases?: CentralDatabaseChoice[] };
  if (!response.ok) {
    throw new Error(body.message || "تعذر البحث عن القواعد المركزية.");
  }
  return body.databases ?? [];
}

function ExistingCentralConnection({
  value,
  preview,
  pending,
  onChange,
  onTest,
  onSimulate,
}: {
  value: BranchSetupInput;
  preview: boolean;
  pending: boolean;
  onChange: (next: Partial<BranchSetupInput>) => void;
  onTest: (credentials: { username: string; password: string }) => void;
  onSimulate: (credentials: { username: string; password: string }) => void;
}) {
  const [username, setUsername] = useState(value.centralUsername);
  const [password, setPassword] = useState("");
  const formReady = connectionInputsReady({
    remote: true,
    host: value.centralServer,
    databaseName: value.centralDatabaseName,
    authentication: value.centralAuthentication === "Sql" ? "Sql" : "Integrated",
    username,
    password,
  });
  return (
    <div className="connection-fields">
      <p>أدخل بيانات القاعدة المركزية الموجودة ثم اختبر الاتصال.</p>
      <div className="connection-pair">
        <label className="field">
          <span>الخادم / عنوان IP</span>
          <input className="latin-input" value={value.centralServer} onChange={(event) => onChange({ centralServer: event.target.value, centralConnectionTested: false })} />
        </label>
        <label className="field">
          <span>المنفذ</span>
          <input className="latin-input" inputMode="numeric" value={value.centralPort} onChange={(event) => onChange({ centralPort: event.target.value, centralConnectionTested: false })} />
        </label>
      </div>
      <label className="field">
        <span>اسم الـ Instance (اختياري)</span>
        <input className="latin-input" value={value.centralInstance} onChange={(event) => onChange({ centralInstance: event.target.value, centralConnectionTested: false })} />
      </label>
      <AuthenticationChoice
        name="centralAuthentication"
        authentication={value.centralAuthentication === "Sql" ? "Sql" : "Integrated"}
        username={username}
        password={password}
        onAuthentication={(next) => {
          if (next === "Integrated") {
            setUsername("");
            setPassword("");
            onChange({ centralAuthentication: "Integrated", centralConnectionTested: false, centralUsername: "", centralPassword: "" });
            return;
          }
          onChange({ centralAuthentication: "Sql", centralConnectionTested: false });
        }}
        onUsername={(next) => {
          setUsername(next);
          onChange({ centralUsername: next, centralConnectionTested: false });
        }}
        onPassword={setPassword}
      />
      <label className="field">
        <span>اسم قاعدة البيانات</span>
        <input className="latin-input" value={value.centralDatabaseName} onChange={(event) => onChange({ centralDatabaseName: event.target.value, centralConnectionTested: false })} />
      </label>
      {preview ? (
        <button className="ghost-button" type="button" disabled={!formReady} onClick={() => onSimulate(value.centralAuthentication === "Sql" ? { username, password } : { username: "", password: "" })}>محاكاة نجاح الاتصال</button>
      ) : (
        <button
          className="ghost-button"
          type="button"
          disabled={pending || !formReady}
          onClick={() => onTest(value.centralAuthentication === "Sql" ? { username, password } : { username: "", password: "" })}
        >
          اختبار الاتصال
        </button>
      )}
    </div>
  );
}

function applyBranchDatabase(current: BranchSetupInput, chosen: BranchDatabaseChoice): BranchSetupInput {
  return withConnectionVerified(current, "branch", {
    branchDatabaseName: chosen.databaseName,
    branchName: chosen.branchName,
    branchCode: chosen.branchCode,
    attachedBranchId: chosen.branchId ?? "",
    attachedBranchCode: chosen.branchCode,
    branchManualConnection: false,
    businessName: current.operatingMode === "CentralManagement" ? current.businessName : (chosen.businessName || current.businessName),
  });
}

async function discoverCompatibleBranches(businessName: string): Promise<BranchDatabaseChoice[]> {
  const query = businessName ? `?businessName=${encodeURIComponent(businessName)}` : "";
  const response = await fetch(`/api/setup/branch-databases${query}`);
  const body = await response.json() as { message?: string; databases?: BranchDatabaseChoice[] };
  if (!response.ok) {
    throw new Error(body.message || "تعذر البحث عن قواعد الفرع.");
  }
  return body.databases ?? [];
}

function LocalBranchLookup({
  preview,
  lookup,
  choices,
  selectedName,
  branchName,
  onReveal,
  onSelect,
  onManual,
}: {
  preview: boolean;
  lookup: "pending" | "done";
  choices: BranchDatabaseChoice[];
  selectedName: string;
  branchName: string;
  onReveal: (choices: BranchDatabaseChoice[]) => void;
  onSelect: (choice: BranchDatabaseChoice) => void;
  onManual: () => void;
}) {
  if (import.meta.env.DEV && preview && lookup === "pending") {
    return (
      <PreviewTools>
        <button className="ghost-button" type="button" onClick={() => onReveal(PREVIEW_ONE_LOCAL_BRANCH)}>محاكاة فرع واحد</button>
        <button className="ghost-button" type="button" onClick={() => onReveal(PREVIEW_MANY_LOCAL_BRANCHES)}>محاكاة عدة فروع</button>
        <button className="ghost-button" type="button" onClick={() => onReveal([])}>محاكاة عدم العثور على فرع</button>
      </PreviewTools>
    );
  }

  if (choices.length === 1 && selectedName === choices[0].databaseName) {
    return (
      <div role="status">
        <p>✓ تم العثور على قاعدة بيانات فرع</p>
        <ul className="review-list">
          <li>اسم الفرع: {branchName}</li>
          <li>اسم قاعدة البيانات: {choices[0].databaseName}</li>
          <li>الخادم: localhost</li>
        </ul>
      </div>
    );
  }

  if (choices.length > 1) {
    return (
      <div className="choice-list">
        {choices.map((choice) => (
          <button
            className={selectedName === choice.databaseName ? "type-option is-selected" : "type-option"}
            key={choice.databaseName}
            type="button"
            onClick={() => onSelect(choice)}
          >
            <strong>{choice.branchName}</strong>
            <span>{choice.branchCode}</span>
            <span>{choice.databaseName}</span>
          </button>
        ))}
      </div>
    );
  }

  return (
    <div>
      <p>لم يتم العثور على قاعدة بيانات فرع متوافقة على هذا الجهاز.</p>
      <button className="ghost-button" type="button" onClick={onManual}>إدخال بيانات الاتصال يدويًا</button>
    </div>
  );
}

function BranchManualConnection({
  databaseName,
  authentication,
  preview,
  pending,
  onDatabase,
  onAuthentication,
  onTest,
  onSimulate,
}: {
  databaseName: string;
  authentication: "Integrated" | "Sql";
  preview: boolean;
  pending: boolean;
  onDatabase: (value: string) => void;
  onAuthentication: (value: "Integrated" | "Sql") => void;
  onTest: (credentials: { username: string; password: string }) => void;
  onSimulate: (credentials: { username: string; password: string }) => void;
}) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const sqlLogin = authentication === "Sql";
  const formReady = connectionInputsReady({
    remote: false,
    host: "",
    databaseName,
    authentication,
    username,
    password,
    rejectCentralName: true,
  });
  const credentials = sqlLogin ? { username, password } : { username: "", password: "" };
  return (
    <div className="connection-fields">
      <p>خادم SQL المحلي: localhost</p>
      <label className="field">
        <span>اسم قاعدة البيانات</span>
        <input className="latin-input" value={databaseName} onChange={(event) => onDatabase(event.target.value)} />
      </label>
      <AuthenticationChoice
        name="branchManualAuthentication"
        authentication={authentication}
        username={username}
        password={password}
        onAuthentication={(next) => {
          if (next === "Integrated") {
            setUsername("");
            setPassword("");
          }
          onAuthentication(next);
        }}
        onUsername={setUsername}
        onPassword={setPassword}
      />
      {preview ? (
        <button className="ghost-button" type="button" disabled={!formReady} onClick={() => onSimulate(credentials)}>محاكاة نجاح الاتصال</button>
      ) : (
        <button className="ghost-button" type="button" disabled={pending || !formReady} onClick={() => onTest(credentials)}>اختبار الاتصال</button>
      )}
    </div>
  );
}

function NewLocalBranchDatabase({
  databaseName,
  preview,
  pending,
  onDatabase,
  onSimulate,
  onConfirm,
}: {
  databaseName: string;
  preview: boolean;
  pending: boolean;
  onDatabase: (value: string) => void;
  onSimulate: () => void;
  onConfirm: () => void;
}) {
  const ready = branchDatabaseNameReady(databaseName, preview ? PREVIEW_TAKEN_BRANCH_DATABASES : []);
  return (
    <div className="connection-fields">
      <p>اسم قاعدة بيانات الفرع على خادم SQL المحلي. لا يلزم اسم مستخدم أو كلمة مرور.</p>
      <label className="field">
        <span>اسم قاعدة البيانات</span>
        <input className="latin-input" value={databaseName} onChange={(event) => onDatabase(event.target.value)} />
      </label>
      {preview ? (
        <button className="ghost-button" type="button" disabled={!ready} onClick={onSimulate}>محاكاة نجاح الاتصال</button>
      ) : (
        <button className="ghost-button" type="button" disabled={pending || !ready} onClick={onConfirm}>تأكيد اسم القاعدة</button>
      )}
    </div>
  );
}

async function defaultConnectionTest(
  value: BranchSetupInput,
  target: "central" | "branch" | "remote" | "secondary",
  credentials?: { username: string; password: string },
) {
  if (target === "secondary") {
    const host = value.secondaryHost.trim() || "localhost";
    const response = await fetch(`http://${host}:${value.secondaryPort || "5080"}/api/setup/branches`);
    if (!response.ok) {
      return { ok: false, message: "تعذر الاتصال بخادم الفرع." };
    }
    const body = await response.json() as { branches?: WizardBranchChoice[] };
    return { ok: true, branches: body.branches ?? [] };
  }

  const response = await fetch("/api/setup/test-connection", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      intent: target === "central" ? value.centralIntent : "existing",
      locationKind: target === "central" ? value.centralLocation : "Remote",
      server: target === "central"
        ? (value.centralLocation === "Remote" ? value.centralServer.trim() : (value.centralServer.trim() || "localhost"))
        : target === "remote"
          ? value.remoteServer.trim()
          : (value.branchLocation === "Remote" ? value.branchServer.trim() : (value.branchServer.trim() || "localhost")),
      databaseName: target === "remote"
        ? value.branchRemoteDatabaseName
        : target === "branch"
          ? value.branchDatabaseName
          : value.centralDatabaseName,
      ...connectionLogin(target, value, credentials),
      encrypt: value.centralEncrypt,
      trustServerCertificate: value.centralTrustServerCertificate,
      expectedRole: target === "central" ? "Central" : target === "remote" ? "BranchRemote" : "Branch",
      instanceName: (target === "remote" ? value.remoteInstance : target === "branch" ? value.branchInstance : value.centralInstance).trim() || null,
      port: connectionPort(target === "remote" ? value.remotePort : target === "branch" ? value.branchPort : value.centralPort),
    }),
  });
  const body = await response.json() as { ok?: boolean; message?: string; businessName?: string };
  return { ok: response.ok && body.ok === true, message: body.message, businessName: body.businessName };
}

function previewBranchCodeProblem(value: BranchSetupInput): string | null {
  if (!value.branchCode.trim()) {
    return null;
  }
  const currentCode = value.branchIntent === "existing" ? value.attachedBranchCode : "";
  return isDuplicateBranchCode(PREVIEW_BRANCH_CODES, value.branchCode, currentCode) ? BRANCH_CODE_DUPLICATE_MESSAGE : null;
}

async function suggestedBranchCode(value: BranchSetupInput): Promise<string> {
  try {
    const response = await fetch("/api/setup/branch-code", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(branchCodeRequest(value)),
    });
    const body = await response.json() as { suggestedCode?: string };
    return response.ok ? body.suggestedCode ?? "" : "";
  } catch {
    return "";
  }
}

async function branchCodeProblem(value: BranchSetupInput): Promise<string | null> {
  if (!value.branchCode.trim()) {
    return null;
  }

  try {
    const response = await fetch("/api/setup/branch-code", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(branchCodeRequest(value, value.branchCode)),
    });
    const body = await response.json() as { available?: boolean; message?: string };
    if (response.ok && body.available !== false) {
      return null;
    }
    return body.message || BRANCH_CODE_DUPLICATE_MESSAGE;
  } catch {
    return "تعذر التحقق من رمز الفرع.";
  }
}

function connectionPort(value: string): number | null {
  const trimmed = value.trim();
  if (!/^[1-9]\d{0,4}$/.test(trimmed)) {
    return null;
  }
  const port = Number(trimmed);
  return port <= 65535 ? port : null;
}
