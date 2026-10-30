import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { visibleSteps, type CentralDatabaseChoice } from "../setup/wizardFlow";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const oneCentral: CentralDatabaseChoice[] = [
  { businessName: "منشأة المركز", databaseName: "XtraPOS_Central", server: "localhost" },
];

const manyCentrals: CentralDatabaseChoice[] = [
  { businessName: "منشأة المركز", databaseName: "XtraPOS_Central", server: "localhost" },
  { businessName: "منشأة ثانية", databaseName: "XtraPOS_Central_Test_Two", server: "localhost" },
];

function Harness({
  discover,
  onSubmit = () => undefined,
}: {
  discover?: () => Promise<CentralDatabaseChoice[]>;
  onSubmit?: (value: BranchSetupInput) => void;
}) {
  const [value, setValue] = useState(emptyBranchSetup);
  return (
    <SetupWizard
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={onSubmit}
      discoverLocalCentrals={discover}
    />
  );
}

async function reachExistingCentral(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByText("خادم / جهاز رئيسي لفرع"));
  await user.click(screen.getByRole("button", { name: "التالي" }));
  await user.click(screen.getByText("نعم"));
  await user.click(screen.getByRole("button", { name: "التالي" }));
  await user.click(screen.getByText("استخدام قاعدة مركزية موجودة"));
  await user.click(screen.getByRole("button", { name: "التالي" }));
}

describe("existing central wizard", () => {
  it("uses the existing-location question and discovers one local central", async () => {
    const user = userEvent.setup();
    const discover = vi.fn(async () => oneCentral);
    render(<Harness discover={discover} />);

    await reachExistingCentral(user);
    expect(screen.getByRole("heading", { name: "أين توجد القاعدة المركزية الموجودة؟" })).toBeTruthy();
    expect(screen.queryByText("أين تريد إنشاء القاعدة المركزية؟")).toBeNull();
    const counter = screen.getByText(/الخطوة \d+/).textContent;

    await user.click(screen.getByText("على هذا الجهاز"));
    expect(screen.getByText(/الخطوة \d+/).textContent).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(discover).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("heading", { name: "تم الاتصال بالإدارة المركزية" })).toBeTruthy();
    expect(screen.getByText("اسم المنشأة: منشأة المركز")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central")).toBeTruthy();
    expect(screen.getByText("الخادم: localhost")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المنشأة")).toBeNull();
    expect(screen.queryByLabelText("الاسم المختصر (اختياري)")).toBeNull();
    expect(screen.queryByLabelText("الوصف (اختياري)")).toBeNull();
    expect(screen.queryByText("أين تريد إنشاء القاعدة المركزية؟")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "كيف تريد إعداد قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
    expect(counter).not.toBe("");
  });

  it("asks which local central to use when several are compatible", async () => {
    const user = userEvent.setup();
    render(<Harness discover={async () => manyCentrals} />);
    await reachExistingCentral(user);
    await user.click(screen.getByText("على هذا الجهاز"));
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "اختيار القاعدة المركزية" })).toBeTruthy();
    expect(screen.getByText("منشأة المركز")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central")).toBeTruthy();
    expect(screen.getAllByText("الخادم: localhost").length).toBeGreaterThan(0);
    expect(screen.getByText("منشأة ثانية")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central_Test_Two")).toBeTruthy();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.queryByText("أين تريد إنشاء القاعدة المركزية؟")).toBeNull();
    const counter = screen.getByText(/الخطوة \d+/).textContent;

    await user.click(screen.getByText("منشأة ثانية"));
    expect(screen.getByText(/الخطوة \d+/).textContent).toBe(counter);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "تم الاتصال بالإدارة المركزية" })).toBeTruthy();
    expect(screen.getByText("اسم المنشأة: منشأة ثانية")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central_Test_Two")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المنشأة")).toBeNull();

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "كيف تريد إعداد قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
  });

  it("tests a remote existing central and loads its business before branch setup", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(async (_url: string, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { password?: string; expectedRole?: string; intent?: string; databaseName?: string };
      expect(body.password).toBe("Secret-Central-99");
      expect(body.expectedRole).toBe("Central");
      expect(body.intent).toBe("existing");
      expect(body.databaseName).toBe("XtraPOS_Central");
      return { ok: true, json: async () => ({ ok: true, businessName: "منشأة بعيدة" }) };
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<Harness />);

    await reachExistingCentral(user);
    await user.click(screen.getByText("على جهاز / خادم آخر"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    const counter = screen.getByText(/الخطوة \d+/).textContent;

    expect(screen.queryByText("أين تريد إنشاء القاعدة المركزية؟")).toBeNull();
    expect(screen.getByLabelText("الخادم / عنوان IP")).toBeTruthy();
    expect(screen.getByLabelText("المنفذ")).toBeTruthy();
    expect(screen.getByLabelText("اسم الـ Instance (اختياري)")).toBeTruthy();
    expect(screen.getByText("طريقة المصادقة")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.getByLabelText("اسم قاعدة البيانات")).toBeTruthy();
    expect(screen.getByRole("button", { name: "اختبار الاتصال" })).toBeTruthy();
    expect(screen.getByText(/الخطوة \d+/).textContent).toBe(counter);

    await user.click(screen.getByText("مصادقة SQL Server"));
    expect(screen.getByLabelText("اسم المستخدم")).toBeTruthy();
    expect(screen.getByLabelText("كلمة المرور")).toBeTruthy();
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "10.0.0.8");
    await user.type(screen.getByLabelText("المنفذ"), "1433");
    await user.type(screen.getByLabelText("اسم المستخدم"), "central-user");
    await user.type(screen.getByLabelText("كلمة المرور"), "Secret-Central-99");
    const database = screen.getByLabelText("اسم قاعدة البيانات");
    await user.clear(database);
    await user.type(database, "XtraPOS_Central");
    expect(screen.getByText(/الخطوة \d+/).textContent).toBe(counter);
    await user.click(screen.getByRole("button", { name: "اختبار الاتصال" }));
    expect(screen.getByText("تم اختبار الاتصال.")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "تم الاتصال بالإدارة المركزية" })).toBeTruthy();
    expect(screen.getByText("اسم المنشأة: منشأة بعيدة")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central")).toBeTruthy();
    expect(screen.getByText("الخادم: 10.0.0.8")).toBeTruthy();
    expect(screen.queryByText("Secret-Central-99")).toBeNull();
    expect(screen.queryByLabelText("اسم المنشأة")).toBeNull();
    expect(screen.queryByText("أين تريد إنشاء القاعدة المركزية؟")).toBeNull();

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "كيف تريد إعداد قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
  });

  it("keeps the new central path on the create screens", () => {
    const created = visibleSteps({
      ...emptyBranchSetup,
      deviceRole: "CentralOnly",
      centralIntent: "new",
      centralLocation: "Remote",
    });
    expect(created).toEqual(["deviceRole", "centralIntent", "centralPlace", "centralConnection", "centralBusiness", "centralReview"]);

    const existing = visibleSteps({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "CentralManagement",
      centralIntent: "existing",
      centralLocation: "Local",
    });
    expect(existing).toContain("centralLinked");
    expect(existing).not.toContain("centralBusiness");
    expect(existing).not.toContain("centralConnection");
    expect(existing).not.toContain("businessIdentity");
    expect(existing).toContain("branchIntent");
    expect(existing.indexOf("centralLinked")).toBeLessThan(existing.indexOf("branchIntent"));
  });
});
