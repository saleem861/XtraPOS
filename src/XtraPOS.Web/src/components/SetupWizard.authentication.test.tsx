import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { visibleSteps, type WizardStepId } from "../setup/wizardFlow";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const centralExistingRemote: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "CentralOnly",
  centralIntent: "existing",
  centralLocation: "Remote",
  centralServer: "10.0.0.8",
  centralDatabaseName: "XtraPOS_Central",
};

const centralNewRemote: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "CentralOnly",
  centralIntent: "new",
  centralLocation: "Remote",
  centralServer: "qa-central",
  centralDatabaseName: "XtraPOS_Central",
};

const branchExistingRemote: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch",
  operatingMode: "CentralManagement",
  centralIntent: "existing",
  centralLocation: "Local",
  businessName: "منشأة المركز",
  centralDatabaseName: "XtraPOS_Central",
  centralConnectionTested: true,
  branchIntent: "existing",
  branchLocation: "Remote",
  branchServer: "qa-branch",
  branchDatabaseName: "XtraPOS_Branch",
};

const branchNewRemote: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch",
  operatingMode: "Standalone",
  branchIntent: "new",
  branchLocation: "Remote",
  branchServer: "qa-branch",
  branchDatabaseName: "XtraPOS_Branch",
};

const branchRemoteCopy: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch",
  operatingMode: "Standalone",
  branchIntent: "new",
  branchLocation: "Local",
  branchRemoteChoice: "yes",
  remoteServer: "qa-remote",
  branchRemoteDatabaseName: "XtraPOS_Branch",
};

const forms: Array<{ name: string; draft: BranchSetupInput; step: WizardStepId }> = [
  { name: "central existing remote", draft: centralExistingRemote, step: "centralConnection" },
  { name: "central new remote", draft: centralNewRemote, step: "centralConnection" },
  { name: "branch existing remote", draft: branchExistingRemote, step: "branchConnection" },
  { name: "branch new remote", draft: branchNewRemote, step: "branchConnection" },
  { name: "branch remote copy", draft: branchRemoteCopy, step: "remoteConnection" },
];

function renderStep(draft: BranchSetupInput, step: WizardStepId) {
  const initialIndex = visibleSteps(draft).indexOf(step);
  function Harness() {
    const [value, setValue] = useState(draft);
    return (
      <SetupWizard
        value={value}
        onChange={setValue}
        onCancel={() => undefined}
        onSubmit={() => undefined}
        initialIndex={initialIndex}
      />
    );
  }
  render(<Harness />);
}

function testButton() {
  return screen.getByRole("button", { name: "اختبار الاتصال" }) as HTMLButtonElement;
}

describe("connection authentication", () => {
  it.each(forms)("$name hides credentials for Windows authentication", ({ draft, step }) => {
    renderStep(draft, step);

    expect(screen.getByText("طريقة المصادقة")).toBeTruthy();
    expect(screen.getByText("مصادقة ويندوز")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.getByLabelText("اسم قاعدة البيانات")).toBeTruthy();
    expect(screen.getByLabelText("الخادم / عنوان IP")).toBeTruthy();
    expect(testButton().disabled).toBe(false);
  });

  it("keeps port, instance, and transport fields on the central remote forms", () => {
    renderStep(centralNewRemote, "centralConnection");
    expect(screen.getByLabelText("المنفذ")).toBeTruthy();
    expect(screen.getByLabelText("اسم الـ Instance (اختياري)")).toBeTruthy();
    expect(screen.getByText("Encrypt")).toBeTruthy();
    expect(screen.getByText("TrustServerCertificate")).toBeTruthy();
  });

  it.each(forms)("$name requires both SQL credentials and hides them again for Windows", async ({ draft, step }) => {
    const user = userEvent.setup();
    renderStep(draft, step);

    await user.click(screen.getByText("مصادقة SQL Server"));
    expect(screen.getByLabelText("اسم المستخدم")).toBeTruthy();
    expect(screen.getByLabelText("كلمة المرور")).toBeTruthy();
    expect(testButton().disabled).toBe(true);

    await user.type(screen.getByLabelText("اسم المستخدم"), "branch-user");
    expect(testButton().disabled).toBe(true);
    await user.type(screen.getByLabelText("كلمة المرور"), "Secret-1");
    expect(testButton().disabled).toBe(false);

    await user.click(screen.getByText("مصادقة ويندوز"));
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.getByLabelText("اسم قاعدة البيانات")).toBeTruthy();
    expect(testButton().disabled).toBe(false);
  });

  it("does not send credentials for Windows authentication", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ ok: true, businessName: "منشأة بعيدة" }) }));
    vi.stubGlobal("fetch", fetchMock);
    renderStep(centralExistingRemote, "centralConnection");

    await user.click(testButton());

    const body = JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body)) as {
      authenticationType?: string;
      username?: string | null;
      password?: string | null;
      server?: string;
      databaseName?: string;
    };
    expect(body.authenticationType).toBe("Integrated");
    expect(body.username).toBeNull();
    expect(body.password).toBeNull();
    expect(body.server).toBe("10.0.0.8");
    expect(body.databaseName).toBe("XtraPOS_Central");
  });

  it("sends SQL credentials only after both fields are filled", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ ok: true }) }));
    vi.stubGlobal("fetch", fetchMock);
    renderStep(branchRemoteCopy, "remoteConnection");

    await user.click(screen.getByText("مصادقة SQL Server"));
    await user.type(screen.getByLabelText("اسم المستخدم"), "remote-user");
    await user.type(screen.getByLabelText("كلمة المرور"), "Secret-Remote");
    await user.click(testButton());

    const body = JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body)) as {
      authenticationType?: string;
      username?: string | null;
      password?: string | null;
      expectedRole?: string;
      databaseName?: string;
    };
    expect(body.authenticationType).toBe("Sql");
    expect(body.username).toBe("remote-user");
    expect(body.password).toBe("Secret-Remote");
    expect(body.expectedRole).toBe("BranchRemote");
    expect(body.databaseName).toBe("XtraPOS_Branch");
  });
});
