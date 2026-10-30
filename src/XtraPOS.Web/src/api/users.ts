import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";

export interface ManagedBranch {
  branchId: string;
  branchName: string;
  branchCode: string | null;
}

export interface ManagedRole {
  roleCode: string;
  roleName: string;
}

export interface ManagedUser {
  userId: string;
  fullName: string;
  employeeNumber: string | null;
  username: string;
  roleCode: string | null;
  roleName: string | null;
  isActive: boolean;
  mustChangePassword: boolean;
  lastLoginAt: string | null;
  branches: ManagedBranch[];
}

export interface UserAdminCatalog {
  authority: "Central" | "Branch";
  canManage: boolean;
  canReset: boolean;
  roles: ManagedRole[];
  branches: ManagedBranch[];
}

export interface UserPage {
  authority: "Central" | "Branch";
  count: number;
  users: ManagedUser[];
}

export interface UserQuery {
  search: string;
  active: "" | "active" | "inactive";
  role: string;
  branchId: string;
}

export interface UserDraft {
  fullName: string;
  employeeNumber: string;
  username: string;
  roleCode: string;
  branchIds: string[];
  isActive: boolean;
  passwordMode: "auto" | "manual";
  password: string;
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) {
    throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  }

  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { message?: string };
      if (body.message?.trim()) {
        message = body.message;
      }
    } catch {
      message = fallback;
    }
    throw new SetupClientError(message);
  }

  return (await response.json()) as T;
}

function queryString(query: UserQuery) {
  const params = new URLSearchParams();
  if (query.search.trim()) {
    params.set("search", query.search.trim());
  }
  if (query.active) {
    params.set("active", query.active);
  }
  if (query.role) {
    params.set("role", query.role);
  }
  if (query.branchId) {
    params.set("branchId", query.branchId);
  }
  const text = params.toString();
  return text ? `?${text}` : "";
}

export function createUserClient(session: DeviceSession) {
  const headers = deviceHeaders(session);

  return {
    async catalog() {
      const response = await fetch("/api/users/catalog", { headers });
      return readJson<UserAdminCatalog>(response, "تعذر تحميل بيانات المستخدمين.");
    },
    async list(query: UserQuery) {
      const response = await fetch(`/api/users${queryString(query)}`, { headers });
      return readJson<UserPage>(response, "تعذر عرض المستخدمين.");
    },
    async create(draft: UserDraft) {
      const response = await fetch("/api/users", {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify(draft),
      });
      return readJson<{ user: ManagedUser | null; temporaryPassword: string }>(response, "تعذر إضافة المستخدم.");
    },
    async update(userId: string, draft: Pick<UserDraft, "fullName" | "employeeNumber" | "username">) {
      const response = await fetch(`/api/users/${userId}`, {
        method: "PUT",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify(draft),
      });
      return readJson<ManagedUser>(response, "تعذر تعديل المستخدم.");
    },
    async setActive(userId: string, isActive: boolean) {
      const response = await fetch(`/api/users/${userId}/activation`, {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ isActive }),
      });
      return readJson<ManagedUser>(response, "تعذر تغيير حالة المستخدم.");
    },
    async setRole(userId: string, roleCode: string) {
      const response = await fetch(`/api/users/${userId}/role`, {
        method: "PUT",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ roleCode }),
      });
      return readJson<ManagedUser>(response, "تعذر تغيير الدور.");
    },
    async setBranches(userId: string, branchIds: string[]) {
      const response = await fetch(`/api/users/${userId}/branches`, {
        method: "PUT",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ branchIds }),
      });
      return readJson<ManagedUser>(response, "تعذر تغيير الفروع.");
    },
    async resetPassword(userId: string, passwordMode: "auto" | "manual", password: string) {
      const response = await fetch(`/api/users/${userId}/reset-password`, {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ passwordMode, password: passwordMode === "manual" ? password : null }),
      });
      return readJson<{ user: ManagedUser | null; temporaryPassword: string }>(response, "تعذر إعادة تعيين كلمة المرور.");
    },
  };
}

export type UserClient = ReturnType<typeof createUserClient>;
