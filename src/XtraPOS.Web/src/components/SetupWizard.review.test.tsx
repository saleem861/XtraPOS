import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { isWizardRouteDeterministic, stepCounterLabel, visibleSteps } from "../setup/wizardFlow";
import { mainBranchReview, reviewText, setupTopology } from "../setup/wizardReview";
import { PREVIEW_MANY_LOCAL_CENTRALS } from "../setup/setupPreview";

afterEach(() => cleanup());

const linked = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch" as const,
  operatingMode: "CentralManagement" as const,
  centralIntent: "existing" as const,
  centralLocation: "Remote" as const,
  centralServer: "qa-central",
  centralPort: "1433",
  centralDatabaseName: "XtraPOS_Central",
  businessName: "منشأة المركز",
  branchIntent: "existing" as const,
  branchLocation: "Remote" as const,
  branchServer: "qa-branch",
  branchPort: "1433",
  branchDatabaseName: "XtraPOS_Branch",
  branchName: "مفرق المقاطعة",
  branchCode: "01",
  branchType: "Retail" as const,
  stationName: "كاونتر 1",
  deviceName: "الخادم",
  branchRemoteChoice: "yes" as const,
  remoteServer: "qa-remote",
  remotePort: "1433",
  branchRemoteDatabaseName: "Shop1Remote",
  centralPassword: "Secret-Central",
  branchPassword: "Secret-Branch",
  remotePassword: "Secret-Remote",
  password: "Admin1234",
};

describe("setup review", () => {
  it("builds a central-linked main branch review without secrets", () => {
    const sections = mainBranchReview(linked);
    const text = reviewText(sections);
    expect(sections.map((section) => section.title)).toEqual([
      "الإدارة المركزية",
      "هذا الفرع",
      "نقطة البيع والجهاز",
      "النسخة البعيدة للفرع",
    ]);
    expect(text).toContain("اسم المنشأة منشأة المركز");
    expect(text).toContain("موقع المركز بعيد");
    expect(text).toContain("الخادم qa-central");
    expect(text).toContain("المنفذ 1433");
    expect(text).toContain("اسم الفرع مفرق المقاطعة");
    expect(text).toContain("رمز الفرع 01");
    expect(text).toContain("نوع الفرع متجر بيع");
    expect(text).toContain("قاعدة البيانات موجودة");
    expect(text).toContain("موقع قاعدة الفرع بعيدة");
    expect(text).toContain("خادم قاعدة الفرع qa-branch");
    expect(text).toContain("نقطة البيع كاونتر 1");
    expect(text).toContain("اسم الجهاز الخادم");
    expect(text).toContain("الحالة مفعلة");
    expect(text).toContain("اتجاه المزامنة الفرع المحلي → النسخة البعيدة");
    expect(text).not.toContain("Secret");
    expect(text).not.toContain("Admin1234");
    expect(text).not.toContain("Server=");
    expect(setupTopology(linked)).toEqual(["XtraPOS_Central", "مفرق المقاطعة Local", "مفرق المقاطعة Remote Copy"]);
  });

  it("omits the central section for a standalone branch and marks the remote copy off", () => {
    const sections = mainBranchReview({
      ...linked,
      operatingMode: "Standalone",
      branchRemoteChoice: "no",
    });
    expect(sections.map((section) => section.title)).toEqual([
      "هذا الفرع",
      "نقطة البيع والجهاز",
      "النسخة البعيدة للفرع",
    ]);
    expect(reviewText(sections)).toContain("غير مفعلة");
    expect(setupTopology({ ...linked, operatingMode: "Standalone", branchRemoteChoice: "no" })).toEqual(["مفرق المقاطعة Local"]);
  });

  it("does not announce a final count before the route is settled", () => {
    const existingLocal = {
      ...emptyBranchSetup,
      deviceRole: "CentralOnly" as const,
      centralIntent: "existing" as const,
    };
    const beforePlace = visibleSteps(existingLocal);
    expect(beforePlace).toHaveLength(3);
    expect(isWizardRouteDeterministic(existingLocal)).toBe(false);
    expect(stepCounterLabel(2, beforePlace.length, false)).toBe("الخطوة 3");

    const existingRemote = { ...existingLocal, centralLocation: "Remote" as const };
    const remoteSteps = visibleSteps(existingRemote);
    expect(isWizardRouteDeterministic(existingRemote)).toBe(true);
    expect(stepCounterLabel(2, remoteSteps.length, true)).toBe(`الخطوة 3 من ${remoteSteps.length}`);
    expect(remoteSteps.length).toBeGreaterThan(beforePlace.length);

    const discovered = { ...existingLocal, centralLocation: "Local" as const };
    expect(isWizardRouteDeterministic(discovered, PREVIEW_MANY_LOCAL_CENTRALS)).toBe(true);
    expect(visibleSteps(discovered, [], PREVIEW_MANY_LOCAL_CENTRALS)).toContain("centralSelect");

    const branchBase = {
      ...emptyBranchSetup,
      deviceRole: "MainBranch" as const,
      operatingMode: "Standalone" as const,
      branchIntent: "new" as const,
      branchLocation: "Local" as const,
    };
    expect(isWizardRouteDeterministic(branchBase)).toBe(false);
    expect(isWizardRouteDeterministic({ ...branchBase, branchIntent: "existing" })).toBe(false);
    const withoutCopy = { ...branchBase, branchRemoteChoice: "no" as const };
    const withCopy = { ...branchBase, branchRemoteChoice: "yes" as const };
    expect(isWizardRouteDeterministic(withoutCopy)).toBe(true);
    expect(isWizardRouteDeterministic(withCopy)).toBe(true);
    expect(visibleSteps(withCopy).length).toBe(visibleSteps(withoutCopy).length + 1);
  });
});

describe("preview discovery controls", () => {
  it("offers one, many, and no local central scenarios", async () => {
    const user = userEvent.setup();
    function Harness() {
      const [value, setValue] = useState<BranchSetupInput>({
        ...emptyBranchSetup,
        deviceRole: "CentralOnly",
        centralIntent: "existing",
        centralLocation: "Local",
      });
      const steps = visibleSteps(value);
      return (
        <SetupWizard
          value={value}
          onChange={setValue}
          onCancel={() => undefined}
          onSubmit={() => undefined}
          preview
          initialIndex={steps.indexOf("centralPlace")}
        />
      );
    }
    render(<Harness />);
    expect(screen.getByText("أدوات المعاينة")).toBeTruthy();
    expect(screen.getByText(/^الخطوة \d+$/)).toBeTruthy();
    expect(screen.queryByText(/من \d/)).toBeNull();
    await user.click(screen.getByRole("button", { name: "عدة قواعد" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "اختيار القاعدة المركزية" })).toBeTruthy();
    expect(screen.getByText("منشأة المعاينة")).toBeTruthy();
    expect(screen.getByText("منشأة الرمثا")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central")).toBeTruthy();
    expect(screen.getAllByText("الخادم: localhost").length).toBeGreaterThan(0);
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
  });

  it("clears a connection message when leaving the step", async () => {
    const user = userEvent.setup();
    function Harness() {
      const [value, setValue] = useState<BranchSetupInput>({
        ...emptyBranchSetup,
        deviceRole: "CentralOnly",
        centralIntent: "new",
        centralLocation: "Remote",
        centralServer: "qa-central",
        centralDatabaseName: "XtraPOS_Central",
      });
      const steps = visibleSteps(value);
      return (
        <SetupWizard
          value={value}
          onChange={setValue}
          onCancel={() => undefined}
          onSubmit={() => undefined}
          preview
          initialIndex={steps.indexOf("centralConnection")}
        />
      );
    }
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    expect(screen.getByText("تمت محاكاة نجاح الاتصال. لم يُرسل أي طلب.")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "السابق" }));
    expect(screen.queryByText("تمت محاكاة نجاح الاتصال. لم يُرسل أي طلب.")).toBeNull();
    expect(screen.getByRole("heading", { name: "أين تريد إنشاء القاعدة المركزية؟" })).toBeTruthy();
  });
});
