import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { visibleSteps, type WizardStepId } from "../setup/wizardFlow";
import { BRANCH_CODE_DUPLICATE_MESSAGE } from "../setup/branchCode";
import { withConnectionVerified } from "../setup/connectionContext";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderDraft(draft: BranchSetupInput, step: WizardStepId) {
  const initialIndex = visibleSteps(draft).indexOf(step);
  function Harness() {
    const [value, setValue] = useState(draft);
    return <SetupWizard value={value} onChange={setValue} onCancel={() => undefined} onSubmit={() => undefined} preview initialIndex={initialIndex} />;
  }
  render(<Harness />);
  return initialIndex;
}

describe("setup wizard QA blockers", () => {
  it("keeps remote simulation disabled until the host and database name are entered", () => {
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "CentralOnly",
      centralIntent: "new",
      centralLocation: "Remote",
      centralDatabaseName: "XtraPOS_Central",
    }, "centralConnection");

    const simulate = screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }) as HTMLButtonElement;
    expect(simulate.disabled).toBe(true);
    expect((screen.getByLabelText("الخادم / عنوان IP") as HTMLInputElement).value).toBe("");
    expect(screen.queryByText("localhost")).toBeNull();
  });

  it("does not prefill the branch host from the central host", async () => {
    const user = userEvent.setup();
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "CentralManagement",
      centralIntent: "existing",
      centralLocation: "Remote",
      centralServer: "qa-central",
      centralPort: "1433",
      centralInstance: "SQLEXPRESS",
      centralDatabaseName: "XtraPOS_Central",
      businessName: "منشأة المركز",
      branchIntent: "new",
      branchLocation: "Remote",
    }, "branchConnection");

    const host = screen.getByLabelText("الخادم / عنوان IP") as HTMLInputElement;
    expect(host.value).toBe("");
    expect(screen.queryByDisplayValue("qa-central")).toBeNull();
    expect(screen.queryByDisplayValue("XtraPOS_Central")).toBeNull();

    await user.click(screen.getByText("استخدام نفس خادم الإدارة المركزية"));
    expect((screen.getByLabelText("الخادم / عنوان IP") as HTMLInputElement).value).toBe("qa-central");
    expect((screen.getByLabelText("المنفذ") as HTMLInputElement).value).toBe("1433");
    expect((screen.getByLabelText("اسم الـ Instance (اختياري)") as HTMLInputElement).value).toBe("SQLEXPRESS");
    expect((screen.getByLabelText("اسم قاعدة البيانات") as HTMLInputElement).value).toBe("");

    await user.clear(screen.getByLabelText("الخادم / عنوان IP"));
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "qa-branch");
    expect(screen.getByDisplayValue("qa-branch")).toBeTruthy();
    expect(screen.queryByDisplayValue("qa-central")).toBeNull();
  });

  it("suggests a new local branch database name and never uses the central name", () => {
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "new",
      branchLocation: "Local",
      branchDatabaseName: "XtraPOS_Branch_04",
    }, "branchConnection");

    expect(screen.getByRole("heading", { name: "قاعدة بيانات الفرع المحلية" })).toBeTruthy();
    const database = screen.getByLabelText("اسم قاعدة البيانات") as HTMLInputElement;
    expect(database.value).toBe("XtraPOS_Branch_04");
    expect(database.value).not.toBe("XtraPOS_Central");
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect((screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("uses the existing-branch location question", () => {
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "existing",
      branchLocation: "Local",
    }, "branchPlace");
    expect(screen.getByRole("heading", { name: "أين توجد قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "أين تريد إنشاء قاعدة بيانات هذا الفرع؟" })).toBeNull();
  });

  it("accepts an existing branch's own code and rejects another branch's code", async () => {
    const user = userEvent.setup();
    const draft = {
      ...emptyBranchSetup,
      deviceRole: "MainBranch" as const,
      operatingMode: "Standalone" as const,
      branchIntent: "existing" as const,
      branchLocation: "Local" as const,
      branchName: "مفرق المقاطعة",
      branchCode: "01",
      branchType: "Retail" as const,
      attachedBranchId: "branch-mafraq",
      attachedBranchCode: "01",
      branchDatabaseName: "XtraPOS_Branch",
    };
    renderDraft(draft, "branchIdentity");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.queryByText(BRANCH_CODE_DUPLICATE_MESSAGE)).toBeNull();
    expect(screen.getByRole("heading", { name: "هل تريد الوصول إلى بيانات هذا الفرع من خارج الفرع؟" })).toBeTruthy();
  });

  it("rejects a code that belongs to a different branch", async () => {
    const user = userEvent.setup();
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "existing",
      branchLocation: "Local",
      branchName: "مفرق المقاطعة",
      branchCode: "02",
      branchType: "Retail",
      attachedBranchId: "branch-mafraq",
      attachedBranchCode: "01",
      branchDatabaseName: "XtraPOS_Branch",
    }, "branchIdentity");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText(BRANCH_CODE_DUPLICATE_MESSAGE)).toBeTruthy();
    expect(screen.getByRole("heading", { name: "هوية هذا الفرع" })).toBeTruthy();
  });

  it("clears a loaded branch identity when switching from existing to new", async () => {
    const user = userEvent.setup();
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "CentralManagement",
      centralIntent: "existing",
      centralLocation: "Local",
      businessName: "منشأة المركز",
      centralDatabaseName: "XtraPOS_Central",
      branchIntent: "existing",
      branchLocation: "Local",
      branchName: "مفرق المقاطعة",
      branchCode: "01",
      branchType: "Retail",
      attachedBranchId: "branch-mafraq",
      attachedBranchCode: "01",
      branchDatabaseName: "XtraPOS_Branch",
    }, "branchIntent");

    await user.click(screen.getByText("إنشاء قاعدة بيانات جديدة"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "أين تريد إنشاء قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect((screen.getByLabelText("اسم قاعدة البيانات") as HTMLInputElement).value).toBe("XtraPOS_Branch_04");
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect((screen.getByLabelText("اسم الفرع") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("رمز الفرع") as HTMLInputElement).value).toBe("04");
    expect(screen.getByText("هوية المنشأة تُحمّل من الإدارة المركزية.")).toBeTruthy();
  });

  it("invalidates a branch connection after the host changes", async () => {
    const user = userEvent.setup();
    const verified = withConnectionVerified({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "new",
      branchLocation: "Remote",
      branchServer: "qa-branch",
      branchDatabaseName: "XtraPOS_Branch",
    }, "branch");
    renderDraft(verified, "branchConnection");
    expect((screen.getByRole("button", { name: "التالي" }) as HTMLButtonElement).disabled).toBe(false);
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "-2");
    expect((screen.getByRole("button", { name: "التالي" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("drops a branch-code error when leaving the identity step", async () => {
    const user = userEvent.setup();
    renderDraft({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "new",
      branchLocation: "Local",
      branchName: "فرع جديد",
      branchCode: "02",
      branchType: "Retail",
      branchDatabaseName: "XtraPOS_Branch_04",
    }, "branchIdentity");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText(BRANCH_CODE_DUPLICATE_MESSAGE)).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "السابق" }));
    expect(screen.queryByText(BRANCH_CODE_DUPLICATE_MESSAGE)).toBeNull();
  });
});
