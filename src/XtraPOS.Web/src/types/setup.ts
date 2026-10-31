export const BRANCH_TYPES = ["Retail", "Warehouse", "RetailAndWarehouse"] as const;

export type BranchType = (typeof BRANCH_TYPES)[number];

export const BRANCH_TYPE_LABELS: Record<BranchType, string> = {
  Retail: "متجر بيع",
  Warehouse: "مستودع",
  RetailAndWarehouse: "متجر ومستودع",
};

export const DEVICE_TYPE_POS = "POS" as const;

export const DEVICE_TYPE_LABEL = "نقطة البيع";

export const SETUP_STEPS = [
  { id: "checkServer", label: "فحص خادم قاعدة البيانات" },
  { id: "createDatabase", label: "إنشاء قاعدة بيانات XtraPOS" },
  { id: "createTables", label: "إنشاء الجداول" },
  { id: "createBusiness", label: "إنشاء المنشأة" },
  { id: "createBranch", label: "إنشاء الفرع" },
  { id: "createStation", label: "إنشاء نقطة البيع" },
  { id: "registerDevice", label: "تسجيل الجهاز" },
  { id: "initialSync", label: "مزامنة المستخدمين من المركزية" },
  { id: "createAdmin", label: "التحقق من حساب المدير" },
  { id: "verify", label: "التحقق من سلامة الإعداد" },
] as const;

export type SetupStepId = (typeof SETUP_STEPS)[number]["id"];

export type SetupStepState = "pending" | "active" | "done" | "error";

export interface BranchSetupInput {
  businessName: string;
  businessDisplayName: string;
  phone: string;
  address: string;
  branchName: string;
  branchCode: string;
  branchType: BranchType | "";
  stationNumber: string;
  stationName: string;
  deviceName: string;
  username: string;
  displayName: string;
  fullName: string;
  employeeNumber: string;
  password: string;
  confirmPassword: string;
  centralAdminUserId: string;
  centralAdminRole: string;
  operatingMode: "" | "Standalone" | "CentralManagement";
  centralAction: "" | "create" | "existing";
  centralLocation: "" | "Local" | "Remote";
  centralDatabaseName: string;
  centralServer: string;
  centralPort: string;
  centralInstance: string;
  centralUsername: string;
  centralPassword: string;
  linkToCentral: "" | "yes" | "no";
  branchLocation: "" | "Local" | "Remote";
  branchServer: string;
  branchPort: string;
  branchInstance: string;
  branchAuthentication: "" | "Integrated" | "Sql";
  branchUsername: string;
  branchPassword: string;
  branchUseCentralServer: boolean;
  branchRemoteChoice: "" | "yes" | "no";
  branchRemoteDatabaseName: string;
  remoteServer: string;
  remotePort: string;
  remoteInstance: string;
  remoteAuthentication: "" | "Integrated" | "Sql";
  remoteUsername: string;
  remotePassword: string;
  branchDatabaseName: string;
  branchManualConnection: boolean;
  attachedBranchId: string;
  attachedBranchCode: string;
  centralVerifiedKey: string;
  branchVerifiedKey: string;
  remoteVerifiedKey: string;
  deviceRole: "" | "MainBranch" | "Secondary" | "CentralOnly";
  roleLocked?: boolean;
  centralIntent: "" | "new" | "existing";
  branchIntent: "" | "new" | "existing";
  centralConnectionTested: boolean;
  branchConnectionTested: boolean;
  remoteCopyTested: boolean;
  secondaryMode: "" | "discover" | "manual";
  secondaryHost: string;
  secondaryPort: string;
  secondaryTested: boolean;
  selectedBranchId: string;
  offlineAcknowledged: boolean;
  pairingCode: string;
  pairingVerified: boolean;
  pairingToken: string;
  stationId: string;
  centralEncrypt: boolean;
  centralTrustServerCertificate: boolean;
  centralAuthentication: "" | "Integrated" | "Sql";
}

export const emptyBranchSetup: BranchSetupInput = {
  businessName: "",
  businessDisplayName: "",
  phone: "",
  address: "",
  branchName: "",
  branchCode: "",
  branchType: "",
  stationNumber: "1",
  stationName: "كاونتر 1",
  deviceName: "",
  username: "",
  displayName: "",
  fullName: "",
  employeeNumber: "",
  password: "",
  confirmPassword: "",
  centralAdminUserId: "",
  centralAdminRole: "",
  operatingMode: "",
  centralAction: "",
  centralLocation: "",
  centralDatabaseName: "XtraPOS_Central",
  centralServer: "",
  centralPort: "",
  centralInstance: "",
  centralUsername: "",
  centralPassword: "",
  linkToCentral: "",
  branchLocation: "Local",
  branchServer: "",
  branchPort: "",
  branchInstance: "",
  branchAuthentication: "Integrated",
  branchUsername: "",
  branchPassword: "",
  branchUseCentralServer: false,
  branchRemoteChoice: "",
  branchRemoteDatabaseName: "",
  remoteServer: "",
  remotePort: "",
  remoteInstance: "",
  remoteAuthentication: "Integrated",
  remoteUsername: "",
  remotePassword: "",
  branchDatabaseName: "",
  branchManualConnection: false,
  attachedBranchId: "",
  attachedBranchCode: "",
  centralVerifiedKey: "",
  branchVerifiedKey: "",
  remoteVerifiedKey: "",
  deviceRole: "",
  roleLocked: false,
  centralIntent: "",
  branchIntent: "",
  centralConnectionTested: false,
  branchConnectionTested: false,
  remoteCopyTested: false,
  secondaryMode: "",
  secondaryHost: "",
  secondaryPort: "5080",
  secondaryTested: false,
  selectedBranchId: "",
  offlineAcknowledged: false,
  pairingCode: "",
  pairingVerified: false,
  pairingToken: "",
  stationId: "",
  centralEncrypt: true,
  centralTrustServerCertificate: true,
  centralAuthentication: "Integrated",
};

export interface DeviceSession {
  deviceId: string;
  businessId: string;
  branchId: string;
  stationId: string;
  deviceName: string;
  deviceType: typeof DEVICE_TYPE_POS;
  deviceToken: string;
  registeredAt: string;
  businessName: string;
  businessDisplayName?: string;
  branchName: string;
  branchCode?: string;
  branchType: BranchType;
  stationName: string;
  stationNumber: number;
}

export interface SetupStatusResponse {
  status: "NotConfigured" | "Configuring" | "Configured" | "ConfiguredButAdminMissing" | "Error" | "ConfigurationError" | "DatabaseUnavailable";
  message?: string;
}

export interface SetupStreamEvent {
  type: "progress" | "failed" | "completed";
  step?: SetupStepId;
  state?: "started" | "succeeded";
  message?: string;
  device?: DeviceSession;
  alreadyConfigured?: boolean;
}

export interface AdminSetupInput {
  username: string;
  displayName: string;
  fullName: string;
  employeeNumber: string;
  password: string;
  confirmPassword: string;
}

export interface RoleInfo {
  roleCode: string;
  roleName: string;
}

export interface CurrentUser {
  userId: string;
  username: string;
  displayName: string;
  fullName?: string;
  roles: RoleInfo[];
  permissions: string[];
  businessName: string;
  businessDisplayName?: string;
  branchName: string;
  stationName: string;
  deviceName: string;
  mustChangePassword?: boolean;
}

export function isBranchType(value: unknown): value is BranchType {
  return typeof value === "string" && BRANCH_TYPES.some((type) => type === value);
}
