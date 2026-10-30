import { BRANCH_TYPE_LABELS, type BranchSetupInput } from "../types/setup";
import { displayedServer } from "./connectionContext";
import type { WizardBranchChoice } from "./wizardFlow";

export interface ReviewRow {
  label: string;
  value: string;
}

export interface ReviewSection {
  title: string;
  rows: ReviewRow[];
}

const SYNC_DIRECTION = "الفرع المحلي → النسخة البعيدة";

export function mainBranchReview(draft: BranchSetupInput): ReviewSection[] {
  const sections: ReviewSection[] = [];
  if (draft.operatingMode === "CentralManagement") {
    const centralRows: ReviewRow[] = [
      { label: "اسم المنشأة", value: draft.businessName.trim() },
      { label: "موقع المركز", value: draft.centralLocation === "Remote" ? "بعيد" : "محلي" },
      { label: "الخادم", value: displayedServer(draft.centralLocation, draft.centralServer) },
    ];
    if (draft.centralLocation === "Remote" && draft.centralPort.trim()) {
      centralRows.push({ label: "المنفذ", value: draft.centralPort.trim() });
    }
    centralRows.push({ label: "اسم قاعدة البيانات", value: draft.centralDatabaseName.trim() });
    sections.push({ title: "الإدارة المركزية", rows: centralRows });
  }

  const branchRows: ReviewRow[] = [
    { label: "اسم الفرع", value: draft.branchName.trim() },
    { label: "رمز الفرع", value: draft.branchCode.trim() },
    { label: "نوع الفرع", value: draft.branchType ? BRANCH_TYPE_LABELS[draft.branchType] : "" },
    { label: "قاعدة البيانات", value: draft.branchIntent === "existing" ? "موجودة" : "جديدة" },
    { label: "موقع قاعدة الفرع", value: draft.branchLocation === "Remote" ? "بعيدة" : "محلية" },
  ];
  if (draft.branchLocation === "Remote") {
    branchRows.push({ label: "خادم قاعدة الفرع", value: draft.branchServer.trim() });
    if (draft.branchPort.trim()) {
      branchRows.push({ label: "المنفذ", value: draft.branchPort.trim() });
    }
  }
  branchRows.push({ label: "اسم قاعدة بيانات الفرع", value: draft.branchDatabaseName.trim() });
  sections.push({ title: "هذا الفرع", rows: branchRows });

  sections.push({
    title: "نقطة البيع والجهاز",
    rows: [
      { label: "نقطة البيع", value: draft.stationName.trim() },
      { label: "اسم الجهاز", value: draft.deviceName.trim() },
    ],
  });

  if (draft.branchRemoteChoice === "yes") {
    const remoteRows: ReviewRow[] = [
      { label: "الحالة", value: "مفعلة" },
      { label: "الخادم", value: draft.remoteServer.trim() },
    ];
    if (draft.remotePort.trim()) {
      remoteRows.push({ label: "المنفذ", value: draft.remotePort.trim() });
    }
    remoteRows.push(
      { label: "اسم قاعدة البيانات", value: draft.branchRemoteDatabaseName.trim() },
      { label: "اتجاه المزامنة", value: SYNC_DIRECTION },
    );
    sections.push({ title: "النسخة البعيدة للفرع", rows: remoteRows });
  } else {
    sections.push({
      title: "النسخة البعيدة للفرع",
      rows: [{ label: "الحالة", value: "غير مفعلة" }],
    });
  }

  return sections;
}

export function secondaryDeviceReview(input: {
  branchName: string;
  server: string;
  address: string;
  port: string;
  stationName: string;
  deviceName: string;
  offlineEnabled: boolean;
  pairingVerified: boolean;
}): ReviewSection {
  const rows: ReviewRow[] = [
    { label: "اسم الفرع", value: input.branchName },
    { label: "خادم الفرع", value: input.server },
    { label: "العنوان", value: input.address },
    { label: "المنفذ", value: input.port },
    { label: "نقطة البيع", value: input.stationName },
    { label: "اسم الجهاز", value: input.deviceName },
    { label: "العمل دون اتصال", value: input.offlineEnabled ? "مفعّل" : "غير مفعّل" },
  ];
  if (input.pairingVerified) {
    rows.push({ label: "", value: "تم التحقق من إذن ربط الجهاز" });
  }
  return { title: "", rows };
}

export function setupTopology(draft: BranchSetupInput, branches: WizardBranchChoice[] = []): string[] {
  if (draft.deviceRole === "Secondary") {
    const selected = branches.find((branch) => branch.branchId === draft.selectedBranchId) ?? branches[0];
    const name = selected?.branchName || draft.branchName.trim() || "Shop1";
    return ["Secondary Device", `Branch API / ${name}`];
  }

  const lines: string[] = [];
  if (draft.deviceRole === "CentralOnly" || draft.operatingMode === "CentralManagement") {
    lines.push(draft.centralDatabaseName.trim() || "XtraPOS_Central");
  }
  if (draft.deviceRole === "CentralOnly") {
    return lines;
  }

  const branchName = draft.branchName.trim() || "Shop1";
  lines.push(`${branchName} Local`);
  if (draft.branchRemoteChoice === "yes") {
    lines.push(`${branchName} Remote Copy`);
  }
  return lines;
}

export function reviewText(sections: ReviewSection[]): string {
  return sections.flatMap((section) => section.rows.map((row) => `${row.label} ${row.value}`)).join("\n");
}
