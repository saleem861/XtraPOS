import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { acceptedLocalBranchDatabases, type BranchDatabaseChoice } from "../setup/branchDiscovery";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const branchPlace = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch" as const,
  operatingMode: "CentralManagement" as const,
  centralIntent: "existing" as const,
  centralLocation: "Local" as const,
  centralConnectionTested: true,
  businessName: "منشأة المعاينة",
  branchIntent: "existing" as const,
  branchLocation: "Local" as const,
};

const localBranch: BranchDatabaseChoice = {
  branchName: "مفرق المقاطعة",
  branchCode: "01",
  databaseName: "XtraPOS_Branch",
  businessName: "منشأة المعاينة",
  server: "localhost",
  role: "Branch",
};

function Harness({
  discover,
  preview = false,
  initial = branchPlace,
}: {
  discover?: (businessName: string) => Promise<BranchDatabaseChoice[]>;
  preview?: boolean;
  initial?: BranchSetupInput;
}) {
  const [value, setValue] = useState(initial);
  return (
    <SetupWizard
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={() => undefined}
      preview={preview}
      initialIndex={6}
      discoverLocalBranches={discover}
    />
  );
}

describe("local existing branch database", () => {
  it("discovers one local branch without the generic SQL form or the central database name", async () => {
    const user = userEvent.setup();
    const discover = vi.fn(async () => [
      localBranch,
      { ...localBranch, databaseName: "XtraPOS_Central", role: "Central", branchName: "مركز" },
      { ...localBranch, databaseName: "XtraPOS_Branch_Remote", role: "BranchRemote", branchName: "نسخة بعيدة" },
    ]);
    render(<Harness discover={discover} />);

    expect(screen.getByRole("heading", { name: "أين توجد قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(discover).toHaveBeenCalledWith("منشأة المعاينة");
    expect(screen.getByRole("heading", { name: "اختيار قاعدة بيانات الفرع الموجودة" })).toBeTruthy();
    expect(screen.getByText("✓ تم العثور على قاعدة بيانات فرع")).toBeTruthy();
    expect(screen.getByText("اسم الفرع: مفرق المقاطعة")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Branch")).toBeTruthy();
    expect(screen.getByText("الخادم: localhost")).toBeTruthy();
    expect(screen.queryByText("XtraPOS_Central")).toBeNull();
    expect(screen.queryByText("XtraPOS_Branch_Remote")).toBeNull();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.queryByLabelText("اسم قاعدة البيانات")).toBeNull();

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "هوية هذا الفرع" })).toBeTruthy();
  });

  it("shows selection cards when several compatible branch databases exist", async () => {
    const user = userEvent.setup();
    render(<Harness discover={async () => [
      localBranch,
      {
        branchName: "الرمثا",
        branchCode: "02",
        databaseName: "XtraPOS_Branch_Test_Ramtha",
        businessName: "منشأة المعاينة",
        server: "localhost",
        role: "Branch",
      },
    ]} />);
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "اختيار قاعدة بيانات الفرع الموجودة" })).toBeTruthy();
    expect(screen.getByText("مفرق المقاطعة")).toBeTruthy();
    expect(screen.getByText("01")).toBeTruthy();
    expect(screen.getByText("XtraPOS_Branch")).toBeTruthy();
    expect(screen.getByText("الرمثا")).toBeTruthy();
    expect(screen.getByText("02")).toBeTruthy();
    expect(screen.getByText("XtraPOS_Branch_Test_Ramtha")).toBeTruthy();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.queryByDisplayValue("XtraPOS_Central")).toBeNull();

    await user.click(screen.getByText("الرمثا"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "هوية هذا الفرع" })).toBeTruthy();
    expect(screen.getByDisplayValue("الرمثا")).toBeTruthy();
  });

  it("offers manual connection only after no compatible branch database is found", async () => {
    const user = userEvent.setup();
    render(<Harness discover={async () => []} />);
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByText("لم يتم العثور على قاعدة بيانات فرع متوافقة على هذا الجهاز.")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();

    await user.click(screen.getByRole("button", { name: "إدخال بيانات الاتصال يدويًا" }));
    const database = screen.getByLabelText("اسم قاعدة البيانات") as HTMLInputElement;
    expect(database.value).toBe("");
    expect(database.value).not.toBe("XtraPOS_Central");
    expect(screen.getByText("خادم SQL المحلي: localhost")).toBeTruthy();
    expect(screen.getByText("طريقة المصادقة")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
  });

  it("simulates one, many, and no local branch databases in preview without writing", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(() => {
      throw new Error("preview called the network");
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<Harness preview />);

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("button", { name: "محاكاة فرع واحد" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "محاكاة عدة فروع" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "محاكاة عدم العثور على فرع" })).toBeTruthy();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();

    await user.click(screen.getByRole("button", { name: "محاكاة فرع واحد" }));
    expect(screen.getByText("اسم الفرع: مفرق المقاطعة")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Branch")).toBeTruthy();
    expect(screen.queryByText("XtraPOS_Central")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("keeps central and branch-remote databases out of the local branch list", () => {
    const accepted = acceptedLocalBranchDatabases([
      localBranch,
      { ...localBranch, databaseName: "XtraPOS_Central", role: "Central", branchName: "المركز" },
      { ...localBranch, databaseName: "ShopRemote", role: "BranchRemote", branchName: "نسخة بعيدة" },
      { ...localBranch, databaseName: "XtraPOS_Branch_Test_Other", businessName: "منشأة أخرى", branchName: "فرع آخر" },
    ], "منشأة المعاينة");
    expect(accepted.map((item) => item.databaseName)).toEqual(["XtraPOS_Branch"]);
  });
});
