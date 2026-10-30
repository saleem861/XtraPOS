import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createBranch } from "../api/client";
import { visibleSteps } from "../setup/wizardFlow";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { SetupWizard } from "./SetupWizard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const linked: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch",
  operatingMode: "CentralManagement",
  centralIntent: "existing",
  centralLocation: "Local",
  centralAction: "existing",
  centralDatabaseName: "XtraPOS_Central",
  centralConnectionTested: true,
  linkToCentral: "yes",
  businessName: "منشأة المعاينة",
  branchIntent: "new",
  branchLocation: "Local",
  branchName: "فرع جديد",
  branchCode: "02",
  branchType: "Retail",
  branchRemoteChoice: "no",
  stationNumber: "1",
  stationName: "كاونتر 1",
  deviceName: "صندوق 1",
};

const standalone: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch",
  operatingMode: "Standalone",
  linkToCentral: "no",
  businessName: "منشأة مستقلة",
  branchIntent: "new",
  branchLocation: "Local",
  branchName: "فرع مستقل",
  branchCode: "03",
  branchType: "Retail",
  branchRemoteChoice: "no",
  stationNumber: "1",
  stationName: "كاونتر 1",
  deviceName: "صندوق 1",
};

const centralAdmin = {
  userId: "df9da948-2ec2-f111-b933-d43b046fdf64",
  username: "905017083",
  displayName: "سليم جيوسي",
  fullName: "سليم جيوسي",
  employeeNumber: "905017083",
  roleCode: "Admin",
  roleName: "مدير النظام",
};

function Harness({
  initial,
  start = "admin",
  onSubmit = () => undefined,
}: {
  initial: BranchSetupInput;
  start?: "admin" | "deviceName";
  onSubmit?: (value: BranchSetupInput) => void;
}) {
  const [value, setValue] = useState(initial);
  const steps = visibleSteps(initial);
  return (
    <SetupWizard
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={onSubmit}
      initialIndex={steps.indexOf(start)}
    />
  );
}

describe("central-linked admin step", () => {
  it("loads central admins first and accepts a verified password", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify(
      String(url).includes("verify-central-admin") ? { admin: centralAdmin } : { admins: [centralAdmin] },
    ), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    })));
    const submitted: BranchSetupInput[] = [];
    render(<Harness initial={linked} start="deviceName" onSubmit={(value) => submitted.push(value)} />);

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "التحقق من حساب المدير" })).toBeTruthy();
    await user.type(screen.getByLabelText("اسم المستخدم"), "905017083");
    await user.type(screen.getByLabelText("كلمة المرور"), "Admin1234");
    await user.click(screen.getByRole("button", { name: "تحقق من المدير" }));
    expect(screen.getByText("تم التحقق من حساب المدير.")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByRole("button", { name: "تنفيذ الإعداد" }));

    expect(submitted[0].centralAdminUserId).toBe(centralAdmin.userId);
    expect(submitted[0].username).toBe("905017083");
    expect(submitted[0].displayName).toBe("سليم جيوسي");
    expect(submitted[0].password).toBe("");
    expect(submitted[0].confirmPassword).toBe("");
  });
});

describe("standalone admin step", () => {
  it("keeps username and display name on their own fields and still creates the first admin", async () => {
    const user = userEvent.setup();
    const submitted: BranchSetupInput[] = [];
    render(<Harness initial={standalone} onSubmit={(value) => submitted.push(value)} />);

    expect(screen.getByRole("heading", { name: "حساب المدير" })).toBeTruthy();
    await user.type(screen.getByLabelText("اسم المستخدم"), "905017083");
    await user.type(screen.getByLabelText("الاسم الظاهر"), "سليم جيوسي");
    expect(screen.getByLabelText("اسم المستخدم")).toHaveProperty("value", "905017083");
    expect(screen.getByLabelText("الاسم الظاهر")).toHaveProperty("value", "سليم جيوسي");
    await user.type(screen.getByLabelText("كلمة المرور"), "Admin1234");
    await user.type(screen.getByLabelText("تأكيد كلمة المرور"), "Admin1234");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByRole("button", { name: "تنفيذ الإعداد" }));

    expect(submitted[0].username).toBe("905017083");
    expect(submitted[0].displayName).toBe("سليم جيوسي");
    expect(submitted[0].centralAdminUserId).toBe("");
  });
});

describe("create branch admin payload", () => {
  it("does not swap username and display name, and omits a password for a reused central admin", async () => {
    const bodies: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init?: RequestInit) => {
      bodies.push(String(init?.body ?? ""));
      return new Response(`${JSON.stringify({ type: "completed" })}\n`, {
        status: 200,
        headers: { "Content-Type": "application/x-ndjson" },
      });
    }));

    await createBranch({
      ...linked,
      centralAdminUserId: centralAdmin.userId,
      username: "905017083",
      displayName: "سليم جيوسي",
      fullName: "سليم جيوسي",
      employeeNumber: "905017083",
    }, () => undefined);
    await createBranch({
      ...standalone,
      username: "905017083",
      displayName: "سليم جيوسي",
      password: "Admin1234",
      confirmPassword: "Admin1234",
    }, () => undefined);

    const reused = JSON.parse(bodies[0]) as { username: string; displayName: string; password: string; centralAdminUserId: string };
    const created = JSON.parse(bodies[1]) as { username: string; displayName: string; password: string; centralAdminUserId: string | null };
    expect(reused.username).toBe("905017083");
    expect(reused.displayName).toBe("سليم جيوسي");
    expect(reused.password).toBe("");
    expect(reused.centralAdminUserId).toBe(centralAdmin.userId);
    expect(created.username).toBe("905017083");
    expect(created.displayName).toBe("سليم جيوسي");
    expect(created.password).toBe("Admin1234");
    expect(created.centralAdminUserId).toBeNull();
  });
});
