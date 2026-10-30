import type { BranchSetupInput } from "../types/setup";
import {
  branchDatabaseNameReady,
  connectionInputsReady,
  connectionVerified,
} from "./connectionContext";

export type WizardStepId =
  | "deviceRole"
  | "branchMode"
  | "centralIntent"
  | "centralPlace"
  | "centralConnection"
  | "centralSelect"
  | "centralLinked"
  | "centralBusiness"
  | "centralReview"
  | "branchIntent"
  | "branchPlace"
  | "branchDiscover"
  | "branchConnection"
  | "businessIdentity"
  | "branchIdentity"
  | "remoteCopy"
  | "remoteConnection"
  | "secondaryConnect"
  | "branchSelect"
  | "deviceAuthorization"
  | "posStation"
  | "deviceIdentity"
  | "offlinePrep"
  | "secondaryReview"
  | "deviceName"
  | "admin"
  | "execute";

export interface WizardBranchChoice {
  branchId: string;
  branchName: string;
  databaseName: string;
  branchCode?: string;
}

export interface CentralDatabaseChoice {
  businessName: string;
  databaseName: string;
  server: string;
}

const USERNAME_PATTERN = /^[\p{L}\p{N}._-]{3,100}$/u;

export function isNewBranchDraft(draft: BranchSetupInput): boolean {
  return draft.deviceRole === "MainBranch" && draft.branchIntent === "new";
}

export function isCentralLinkedNewBranch(draft: BranchSetupInput): boolean {
  return isNewBranchDraft(draft)
    && draft.operatingMode === "CentralManagement"
    && draft.linkToCentral === "yes";
}

export function withNewBranchStationDraft(draft: BranchSetupInput): BranchSetupInput {
  return {
    ...draft,
    stationNumber: draft.stationNumber.trim() || "1",
    stationName: draft.stationName.trim() || "كاونتر 1",
  };
}

export function withNewBranchDeviceDraft(draft: BranchSetupInput): BranchSetupInput {
  return {
    ...draft,
    deviceName: draft.deviceName.trim() || "صندوق 1",
  };
}

export function visibleSteps(
  draft: BranchSetupInput,
  branchChoices: WizardBranchChoice[] = [],
  centralChoices: CentralDatabaseChoice[] = [],
): WizardStepId[] {
  const steps: WizardStepId[] = draft.roleLocked && draft.deviceRole !== "" ? [] : ["deviceRole"];
  if (draft.deviceRole === "CentralOnly") {
    appendCentralSteps(steps, draft, centralChoices, true);
    return steps;
  }

  if (draft.deviceRole === "Secondary") {
    steps.push("secondaryConnect");
    if (branchChoices.length > 1) {
      steps.push("branchSelect");
    }
    steps.push("deviceAuthorization", "posStation", "deviceIdentity", "offlinePrep", "secondaryReview");
    return steps;
  }

  if (draft.deviceRole !== "MainBranch") {
    return steps;
  }

  steps.push("branchMode");
  if (draft.operatingMode === "CentralManagement") {
    appendCentralSteps(steps, draft, centralChoices, false);
  }

  steps.push("branchIntent", "branchPlace");
  if (draft.branchIntent === "existing" && draft.branchLocation === "Local") {
    steps.push("branchDiscover");
    if (draft.branchManualConnection) {
      steps.push("branchConnection");
    }
  } else if (draft.branchIntent === "new" && draft.branchLocation === "Local") {
    steps.push("branchConnection");
  } else if (draft.branchIntent === "existing" || draft.branchLocation === "Remote") {
    steps.push("branchConnection");
  }
  if (draft.operatingMode !== "CentralManagement") {
    steps.push("businessIdentity");
  }
  steps.push("branchIdentity", "remoteCopy");
  if (draft.branchRemoteChoice === "yes") {
    steps.push("remoteConnection");
  }
  steps.push("posStation", "deviceName", "admin", "execute");
  return steps;
}

function appendCentralSteps(
  steps: WizardStepId[],
  draft: BranchSetupInput,
  centralChoices: CentralDatabaseChoice[],
  review: boolean,
) {
  steps.push("centralIntent", "centralPlace");
  if (draft.centralIntent === "existing") {
    if (draft.centralLocation === "Remote") {
      steps.push("centralConnection");
    } else if (draft.centralLocation === "Local" && centralChoices.length > 1) {
      steps.push("centralSelect");
    }
    if (draft.centralLocation === "Local" || draft.centralLocation === "Remote") {
      steps.push("centralLinked");
    }
    return;
  }

  steps.push("centralConnection");
  if (draft.centralIntent === "new") {
    steps.push("centralBusiness");
  }
  if (review) {
    steps.push("centralReview");
  }
}

export function canAdvance(
  draft: BranchSetupInput,
  step: WizardStepId,
  branchChoices: WizardBranchChoice[] = [],
  centralChoices: CentralDatabaseChoice[] = [],
): boolean {
  switch (step) {
    case "deviceRole":
      return draft.deviceRole !== "";
    case "branchMode":
      return draft.operatingMode === "Standalone" || draft.operatingMode === "CentralManagement";
    case "centralIntent":
      return draft.centralIntent === "new" || draft.centralIntent === "existing";
    case "centralPlace":
      return draft.centralLocation === "Local" || draft.centralLocation === "Remote";
    case "centralConnection":
      return connectionInputsReady({
        remote: draft.centralLocation === "Remote",
        host: draft.centralServer,
        databaseName: draft.centralDatabaseName,
        authentication: draft.centralAuthentication,
        username: draft.centralUsername,
        password: draft.centralPassword,
      }) && connectionVerified(draft, "central");
    case "centralSelect":
      return centralChoices.some((item) => item.databaseName === draft.centralDatabaseName);
    case "centralLinked":
      return draft.businessName.trim().length > 0 && draft.centralDatabaseName.trim().length > 0;
    case "centralBusiness":
    case "businessIdentity":
      return draft.businessName.trim().length > 0;
    case "centralReview":
    case "execute":
      return true;
    case "branchIntent":
      return draft.branchIntent === "new" || draft.branchIntent === "existing";
    case "branchPlace":
      return draft.branchLocation === "Local" || draft.branchLocation === "Remote";
    case "branchDiscover":
      return connectionVerified(draft, "branch") && draft.branchDatabaseName.trim().length > 0;
    case "branchConnection":
      if (draft.branchIntent === "new" && draft.branchLocation === "Local") {
        return branchDatabaseNameReady(draft.branchDatabaseName) && connectionVerified(draft, "branch");
      }
      return connectionInputsReady({
        remote: draft.branchLocation === "Remote",
        host: draft.branchServer,
        databaseName: draft.branchDatabaseName,
        authentication: draft.branchAuthentication,
        username: draft.branchUsername,
        password: draft.branchPassword,
        rejectCentralName: true,
      }) && connectionVerified(draft, "branch");
    case "branchIdentity":
      return draft.branchName.trim().length > 0 && draft.branchType !== "";
    case "remoteCopy":
      return draft.branchRemoteChoice === "yes" || draft.branchRemoteChoice === "no";
    case "remoteConnection":
      return connectionInputsReady({
        remote: true,
        host: draft.remoteServer,
        databaseName: draft.branchRemoteDatabaseName,
        authentication: draft.remoteAuthentication,
        username: draft.remoteUsername,
        password: draft.remotePassword,
        rejectCentralName: true,
      }) && connectionVerified(draft, "remote");
    case "secondaryConnect":
      return draft.secondaryTested && (draft.secondaryMode === "discover" || draft.secondaryHost.trim().length > 0);
    case "branchSelect":
      return branchChoices.some((branch) => branch.branchId === draft.selectedBranchId);
    case "deviceAuthorization":
      return draft.pairingVerified && draft.pairingToken.trim().length > 0;
    case "posStation":
      return /^[1-9]\d*$/.test(draft.stationNumber.trim()) && draft.stationName.trim().length > 0;
    case "deviceIdentity":
    case "deviceName":
      return draft.deviceName.trim().length > 0;
    case "offlinePrep":
      return draft.offlineAcknowledged;
    case "secondaryReview":
      return true;
    case "admin":
      if (isCentralLinkedNewBranch(draft)) {
        return draft.centralAdminUserId.trim().length > 0;
      }
      return USERNAME_PATTERN.test(draft.username.trim())
        && draft.displayName.trim().length > 0
        && draft.password.length >= 8
        && draft.password === draft.confirmPassword;
    default:
      return false;
  }
}

export function executeLabel(step: WizardStepId, preview = false): string {
  if (preview && isFinalStep(step)) {
    return "إنهاء المعاينة";
  }
  if (step === "centralReview") {
    return "إنشاء القاعدة المركزية";
  }
  if (step === "secondaryReview") {
    return "ربط هذا الجهاز";
  }
  if (step === "execute") {
    return "تنفيذ الإعداد";
  }
  return "التالي";
}

export function isFinalStep(step: WizardStepId): boolean {
  return step === "centralReview" || step === "secondaryReview" || step === "execute";
}

export function isWizardRouteDeterministic(
  draft: BranchSetupInput,
  centralChoices: CentralDatabaseChoice[] = [],
  discoveredBranchCount = 0,
  branchLookupDone = false,
): boolean {
  if (draft.deviceRole === "Secondary") {
    return draft.secondaryTested;
  }
  if (draft.deviceRole === "CentralOnly") {
    return centralRouteSettled(draft, centralChoices);
  }
  if (draft.deviceRole !== "MainBranch") {
    return false;
  }
  if (draft.operatingMode !== "Standalone" && draft.operatingMode !== "CentralManagement") {
    return false;
  }
  if (draft.operatingMode === "CentralManagement" && !centralRouteSettled(draft, centralChoices)) {
    return false;
  }
  if (draft.branchIntent !== "new" && draft.branchIntent !== "existing") {
    return false;
  }
  if (draft.branchLocation !== "Local" && draft.branchLocation !== "Remote") {
    return false;
  }
  if (draft.branchIntent === "existing" && draft.branchLocation === "Local") {
    const discovered = branchLookupDone && discoveredBranchCount > 0;
    if (!draft.branchManualConnection && !discovered) {
      return false;
    }
  }
  return draft.branchRemoteChoice === "yes" || draft.branchRemoteChoice === "no";
}

function centralRouteSettled(draft: BranchSetupInput, centralChoices: CentralDatabaseChoice[]): boolean {
  if (draft.centralIntent === "new") {
    return true;
  }
  if (draft.centralIntent !== "existing") {
    return false;
  }
  if (draft.centralLocation === "Remote") {
    return true;
  }
  if (draft.centralLocation === "Local") {
    return centralChoices.length > 0;
  }
  return false;
}

export function stepCounterLabel(index: number, total: number, deterministic: boolean): string {
  const count = Math.max(total, 1);
  const current = Math.min(Math.max(index, 0), count - 1) + 1;
  if (!deterministic) {
    return `الخطوة ${current}`;
  }
  return `الخطوة ${current} من ${count}`;
}
