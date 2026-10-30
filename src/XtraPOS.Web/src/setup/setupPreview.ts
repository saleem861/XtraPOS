import type { BranchSetupInput } from "../types/setup";
import type { WizardBranchChoice, CentralDatabaseChoice } from "./wizardFlow";
import type { BranchDatabaseChoice } from "./branchDiscovery";
import { setupTopology } from "./wizardReview";

export const SETUP_PREVIEW_PATH = "/setup-preview";
export const SETUP_PREVIEW_BANNER = "وضع معاينة — لن يتم حفظ أو تنفيذ أي تغييرات";

export const PREVIEW_ONE_BRANCH: WizardBranchChoice[] = [
  { branchId: "shop1", branchName: "Shop1", branchCode: "01", databaseName: "XtraPOS_Branch" },
];

export const PREVIEW_MANY_BRANCHES: WizardBranchChoice[] = [
  { branchId: "shop1", branchName: "Shop1", branchCode: "01", databaseName: "XtraPOS_Branch" },
  { branchId: "shop2", branchName: "Shop2", branchCode: "02", databaseName: "XtraPOS_Branch_Test_Ramtha" },
  { branchId: "store", branchName: "المخزن", branchCode: "03", databaseName: "XtraPOS_Branch" },
];

export const PREVIEW_LOCAL_CENTRALS: CentralDatabaseChoice[] = [
  { businessName: "منشأة المعاينة", databaseName: "XtraPOS_Central", server: "localhost" },
];

export const PREVIEW_MANY_LOCAL_CENTRALS: CentralDatabaseChoice[] = [
  { businessName: "منشأة المعاينة", databaseName: "XtraPOS_Central", server: "localhost" },
  { businessName: "منشأة الرمثا", databaseName: "XtraPOS_Central_Test_Ramtha", server: "localhost" },
];

export const PREVIEW_ONE_LOCAL_BRANCH: BranchDatabaseChoice[] = [
  {
    branchName: "مفرق المقاطعة",
    branchCode: "01",
    branchId: "branch-mafraq",
    databaseName: "XtraPOS_Branch",
    businessName: "منشأة المعاينة",
    server: "localhost",
    role: "Branch",
  },
];

export const PREVIEW_MANY_LOCAL_BRANCHES: BranchDatabaseChoice[] = [
  PREVIEW_ONE_LOCAL_BRANCH[0],
  {
    branchName: "الرمثا",
    branchCode: "02",
    branchId: "branch-ramtha",
    databaseName: "XtraPOS_Branch_Test_Ramtha",
    businessName: "منشأة المعاينة",
    server: "localhost",
    role: "Branch",
  },
];

export const PREVIEW_STATIONS = [
  { id: "counter-1", number: "1", name: "كاونتر 1" },
  { id: "counter-2", number: "2", name: "كاونتر 2" },
] as const;

export function isSetupPreviewEnabled(isDevelopment: boolean, pathname: string): boolean {
  return isDevelopment && pathname === SETUP_PREVIEW_PATH;
}

export function previewTopology(draft: BranchSetupInput, branches: WizardBranchChoice[] = []): string[] {
  return setupTopology(draft, branches);
}
