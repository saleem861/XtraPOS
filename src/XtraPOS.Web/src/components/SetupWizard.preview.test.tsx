import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupPreview } from "./SetupPreview";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup } from "../types/setup";
import { BRANCH_CODE_DUPLICATE_MESSAGE, BRANCH_CODE_HELPER } from "../setup/branchCode";
import { executeLabel } from "../setup/wizardFlow";
import { isSetupPreviewEnabled, previewTopology, SETUP_PREVIEW_PATH } from "../setup/setupPreview";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function forbidNetwork() {
  const fetchMock = vi.fn(() => {
    throw new Error("preview called the network");
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

async function chooseAndNext(user: ReturnType<typeof userEvent.setup>, label: string) {
  await user.click(screen.getByText(label));
  await user.click(screen.getByRole("button", { name: "التالي" }));
}

describe("setup preview", () => {
  it("is available only in development at the preview path", () => {
    expect(isSetupPreviewEnabled(true, SETUP_PREVIEW_PATH)).toBe(true);
    expect(isSetupPreviewEnabled(false, SETUP_PREVIEW_PATH)).toBe(false);
    expect(isSetupPreviewEnabled(true, "/")).toBe(false);
    expect(executeLabel("centralReview", true)).toBe("إنهاء المعاينة");
    expect(executeLabel("execute", true)).toBe("إنهاء المعاينة");
    expect(executeLabel("secondaryReview", true)).toBe("إنهاء المعاينة");
    expect(executeLabel("centralReview", false)).toBe("إنشاء القاعدة المركزية");
    expect(executeLabel("execute", false)).toBe("تنفيذ الإعداد");
    expect(executeLabel("secondaryReview", false)).toBe("ربط هذا الجهاز");
    expect(executeLabel("offlinePrep", false)).toBe("التالي");
  });

  it("renders the real wizard while setup is already configured and does not call setup endpoints", async () => {
    const fetchMock = forbidNetwork();
    render(<SetupPreview />);

    expect(screen.getByRole("status").textContent).toContain("وضع معاينة — لن يتم حفظ أو تنفيذ أي تغييرات");
    expect(screen.getByRole("heading", { name: "إعداد XtraPOS" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "تنفيذ الإعداد" })).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("walks the central-only path with simulated connection and exits preview only", async () => {
    const user = userEvent.setup();
    const fetchMock = forbidNetwork();
    render(<SetupPreview />);

    await chooseAndNext(user, "خادم إدارة مركزية فقط");
    await chooseAndNext(user, "إنشاء قاعدة مركزية جديدة");
    await chooseAndNext(user, "على هذا الجهاز");
    expect(screen.getByText(/ليست النسخة البعيدة للفرع/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "اختبار الاتصال" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("اسم المنشأة"), "إكسترا");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "إنشاء القاعدة المركزية" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "إنهاء المعاينة" }));
    expect(screen.getByRole("heading", { name: "انتهت المعاينة" })).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("walks a main branch and keeps the central database distinct from the remote copy", async () => {
    const user = userEvent.setup();
    forbidNetwork();
    render(<SetupPreview />);

    await chooseAndNext(user, "خادم / جهاز رئيسي لفرع");
    await chooseAndNext(user, "نعم");
    await chooseAndNext(user, "استخدام قاعدة مركزية موجودة");
    expect(screen.getByRole("heading", { name: "أين توجد القاعدة المركزية الموجودة؟" })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "أين تريد إنشاء القاعدة المركزية؟" })).toBeNull();
    await user.click(screen.getByText("على هذا الجهاز"));
    await user.click(screen.getByRole("button", { name: "قاعدة واحدة" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "تم الاتصال بالإدارة المركزية" })).toBeTruthy();
    expect(screen.queryByLabelText("اسم المنشأة")).toBeNull();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await chooseAndNext(user, "إنشاء قاعدة بيانات جديدة");
    await chooseAndNext(user, "على هذا الجهاز");
    expect(screen.getByRole("heading", { name: "قاعدة بيانات الفرع المحلية" })).toBeTruthy();
    expect((screen.getByLabelText("اسم قاعدة البيانات") as HTMLInputElement).value).toBe("XtraPOS_Branch_04");
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.queryByText("اسم المنشأة")).toBeNull();
    await user.type(screen.getByLabelText("اسم الفرع"), "Shop1");
    await user.click(screen.getByText("متجر بيع"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await chooseAndNext(user, "نعم، أريد نسخة بعيدة متزامنة");
    expect(screen.getByText("النسخة البعيدة للفرع نسخة تشغيلية/متابعة لهذا الفرع، وليست القاعدة المركزية. الاتجاه الطبيعي: الفرع المحلي → النسخة البعيدة. الاستعادة العكسية عملية منفصلة لاستعادة الفرع.")).toBeTruthy();
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "qa-branch");
    await user.type(screen.getByLabelText("اسم قاعدة البيانات"), "Shop1Remote");
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("اسم الجهاز"), "الخادم");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "مدير الفرع الأولي" })).toBeTruthy();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.getByText("مدير المعاينة")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getAllByText("XtraPOS_Central").length).toBeGreaterThan(0);
    expect(screen.getByText("Shop1 Local")).toBeTruthy();
    expect(screen.getByText("Shop1 Remote Copy")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: Shop1Remote")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "الإدارة المركزية" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "نقطة البيع والجهاز" })).toBeTruthy();
    expect(screen.queryByText("Admin1234")).toBeNull();
    expect(screen.getByRole("button", { name: "إنهاء المعاينة" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "تنفيذ الإعداد" })).toBeNull();
  });

  it("simulates one discovered branch and skips branch selection", async () => {
    const user = userEvent.setup();
    const fetchMock = forbidNetwork();
    render(<SetupPreview />);

    await chooseAndNext(user, "جهاز إضافي داخل فرع موجود");
    expect(screen.getByText("البحث تلقائيًا على الشبكة المحلية")).toBeTruthy();
    expect(screen.getByText("إدخال عنوان الخادم يدويًا")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "محاكاة فرع واحد" })).toBeNull();
    expect(screen.queryByRole("button", { name: "محاكاة عدة فروع" })).toBeNull();
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    await user.click(screen.getByRole("button", { name: "خادم يعيد فرعًا واحدًا" }));
    expect(screen.getByText("✓ تم الاتصال بخادم XtraPOS")).toBeTruthy();
    expect(screen.getByText("الخادم: Shop1")).toBeTruthy();
    expect(screen.getByText("العنوان: localhost")).toBeTruthy();
    expect(screen.getByText("المنفذ: 5080")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "السماح بربط هذا الجهاز" })).toBeTruthy();
    expect(screen.getByText("الخطوة 3 من 7")).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "اختيار الفرع" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "اختيار نقطة البيع" })).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("uses a stable central-only count, Arabic connection fields, and an Arabic review", async () => {
    const user = userEvent.setup();
    forbidNetwork();
    render(<SetupPreview />);

    expect(screen.getByText("الخطوة 1")).toBeTruthy();
    expect(screen.queryByText(/من \d/)).toBeNull();

    await chooseAndNext(user, "خادم إدارة مركزية فقط");
    expect(screen.getByText("الخطوة 2")).toBeTruthy();
    expect(screen.queryByText(/من \d/)).toBeNull();
    await user.click(screen.getByText("إنشاء قاعدة مركزية جديدة"));
    expect(screen.getByText("الخطوة 2 من 6")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText("الخطوة 3 من 6")).toBeTruthy();
    await chooseAndNext(user, "على جهاز / خادم آخر");

    expect(screen.getByText("الخطوة 4 من 6")).toBeTruthy();
    expect(screen.getByText(/القاعدة المركزية هي مرجع المنشأة والفروع/)).toBeTruthy();
    expect(screen.getByLabelText("الخادم / عنوان IP")).toBeTruthy();
    expect(screen.getByLabelText("المنفذ")).toBeTruthy();
    expect(screen.getByLabelText("اسم الـ Instance (اختياري)")).toBeTruthy();
    expect(screen.getByText("طريقة المصادقة")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.getByLabelText("اسم قاعدة البيانات")).toBeTruthy();
    expect(screen.queryByLabelText("Server / Host")).toBeNull();
    expect(screen.queryByRole("button", { name: "اختبار الاتصال" })).toBeNull();
    expect(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" })).toBeTruthy();

    await user.click(screen.getByText("مصادقة SQL Server"));
    expect(screen.getByLabelText("اسم المستخدم")).toBeTruthy();
    expect(screen.getByLabelText("كلمة المرور")).toBeTruthy();
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "69.30.235.148");
    await user.type(screen.getByLabelText("المنفذ"), "10173");
    await user.type(screen.getByLabelText("اسم المستخدم"), "preview-user");
    await user.type(screen.getByLabelText("كلمة المرور"), "SecretValue");
    const database = screen.getByLabelText("اسم قاعدة البيانات");
    await user.clear(database);
    await user.type(database, "XtraPOS_Central_2");
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "هوية المنشأة" })).toBeTruthy();
    expect(screen.getByText("الخطوة 5 من 6")).toBeTruthy();
    await user.type(screen.getByLabelText("اسم المنشأة"), "SmokerWorld");
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByText("الخطوة 6 من 6")).toBeTruthy();
    expect(screen.getByText("نوع الإعداد: إدارة مركزية")).toBeTruthy();
    expect(screen.getByText("موقع القاعدة: بعيد")).toBeTruthy();
    expect(screen.getByText("الخادم: 69.30.235.148")).toBeTruthy();
    expect(screen.getByText("المنفذ: 10173")).toBeTruthy();
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central_2")).toBeTruthy();
    expect(screen.getByText("اسم المنشأة: SmokerWorld")).toBeTruthy();
    expect(screen.queryByText(/SecretValue/)).toBeNull();
    expect(screen.queryByText(/Server:/)).toBeNull();
    expect(screen.queryByText(/Database name:/)).toBeNull();
    expect(screen.getByRole("button", { name: "إنهاء المعاينة" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "إنشاء القاعدة المركزية" })).toBeNull();
  });

  it("suggests branch code 04 in preview and keeps an edited unique code", async () => {
    const user = userEvent.setup();
    const fetchMock = forbidNetwork();
    render(<SetupPreview />);

    await chooseAndNext(user, "خادم / جهاز رئيسي لفرع");
    await chooseAndNext(user, "لا");
    await chooseAndNext(user, "إنشاء قاعدة بيانات جديدة");
    expect(screen.getByRole("heading", { name: "أين تريد إنشاء قاعدة بيانات هذا الفرع؟" })).toBeTruthy();
    await chooseAndNext(user, "على هذا الجهاز");
    expect(screen.getByRole("heading", { name: "قاعدة بيانات الفرع المحلية" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "محاكاة نجاح الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("اسم المنشأة"), "إكسترا");
    await user.click(screen.getByRole("button", { name: "التالي" }));

    const code = screen.getByLabelText("رمز الفرع") as HTMLInputElement;
    expect(code.value).toBe("04");
    expect(screen.getByText(BRANCH_CODE_HELPER)).toBeTruthy();
    await user.clear(code);
    await user.type(code, "02");
    await user.type(screen.getByLabelText("اسم الفرع"), "Shop1");
    await user.click(screen.getByText("متجر بيع"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText(BRANCH_CODE_DUPLICATE_MESSAGE)).toBeTruthy();
    expect(screen.getByRole("heading", { name: "هوية هذا الفرع" })).toBeTruthy();

    await user.clear(code);
    await user.type(code, "09");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "هل تريد الوصول إلى بيانات هذا الفرع من خارج الفرع؟" })).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("keeps the real connection test on the normal wizard", async () => {
    const user = userEvent.setup();
    function Harness() {
      const [value, setValue] = useState({ ...emptyBranchSetup, deviceRole: "CentralOnly" as const });
      return <SetupWizard value={value} onChange={setValue} onCancel={() => undefined} onSubmit={() => undefined} />;
    }
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("إنشاء قاعدة مركزية جديدة"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("على هذا الجهاز"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("button", { name: "اختبار الاتصال" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "محاكاة نجاح الاتصال" })).toBeNull();
  });

  it("sends host and port as separate connection fields", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ ok: true }) }));
    vi.stubGlobal("fetch", fetchMock);
    function Harness() {
      const [value, setValue] = useState({ ...emptyBranchSetup, deviceRole: "CentralOnly" as const });
      return <SetupWizard value={value} onChange={setValue} onCancel={() => undefined} onSubmit={() => undefined} />;
    }
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("إنشاء قاعدة مركزية جديدة"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("على جهاز / خادم آخر"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "69.30.235.148");
    await user.type(screen.getByLabelText("المنفذ"), "10173");
    await user.type(screen.getByLabelText("اسم الـ Instance (اختياري)"), "SQLEXPRESS");
    await user.click(screen.getByRole("button", { name: "اختبار الاتصال" }));

    const body = JSON.parse(String(fetchMock.mock.calls[0][1].body)) as {
      server: string;
      port: number;
      instanceName: string;
    };
    expect(fetchMock.mock.calls[0][0]).toBe("/api/setup/test-connection");
    expect(body.server).toBe("69.30.235.148");
    expect(body.port).toBe(10173);
    expect(body.instanceName).toBe("SQLEXPRESS");
    expect(body.server).not.toContain(",");
    expect(screen.queryByRole("button", { name: "محاكاة نجاح الاتصال" })).toBeNull();
  });

  it("describes a secondary device under its branch server", () => {
    const lines = previewTopology(
      { ...emptyBranchSetup, deviceRole: "Secondary", selectedBranchId: "shop1" },
      [{ branchId: "shop1", branchName: "Shop1", databaseName: "XtraPOS_Branch" }],
    );
    expect(lines).toEqual(["Secondary Device", "Branch API / Shop1"]);
  });
});
