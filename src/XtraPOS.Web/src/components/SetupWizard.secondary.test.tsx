import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { PREVIEW_MANY_BRANCHES, PREVIEW_ONE_BRANCH } from "../setup/setupPreview";
import type { WizardBranchChoice } from "../setup/wizardFlow";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function Harness({
  preview = false,
  branches = PREVIEW_ONE_BRANCH,
  onProbe,
}: {
  preview?: boolean;
  branches?: WizardBranchChoice[];
  onProbe?: (value: BranchSetupInput) => void;
}) {
  const [value, setValue] = useState<BranchSetupInput>({ ...emptyBranchSetup, deviceRole: "Secondary" });
  return (
    <SetupWizard
      preview={preview}
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={() => undefined}
      testConnection={async (current, target) => {
        onProbe?.(current);
        expect(target).toBe("secondary");
        return { ok: true, branches };
      }}
    />
  );
}

async function openSecondary(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: "التالي" }));
}

describe("secondary device connection", () => {
  it("shows only the two connection methods to a normal user", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await openSecondary(user);

    expect(screen.getByText("البحث تلقائيًا على الشبكة المحلية")).toBeTruthy();
    expect(screen.getByText("إدخال عنوان الخادم يدويًا")).toBeTruthy();
    expect(screen.getAllByRole("radio")).toHaveLength(2);
    expect(screen.queryByRole("button", { name: "محاكاة فرع واحد" })).toBeNull();
    expect(screen.queryByRole("button", { name: "محاكاة عدة فروع" })).toBeNull();
    expect(screen.queryByRole("button", { name: "محاكاة العثور على خادم" })).toBeNull();
    expect(screen.queryByRole("button", { name: "محاكاة نجاح الاتصال" })).toBeNull();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    expect(screen.queryByLabelText("اسم قاعدة البيانات")).toBeNull();
  });

  it("keeps the server-found simulation in preview only", async () => {
    const user = userEvent.setup();
    render(<Harness preview />);
    await openSecondary(user);
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    expect(screen.getByRole("button", { name: "البحث الآن" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "خادم يعيد فرعًا واحدًا" })).toBeTruthy();
    expect(screen.getByText("أدوات المعاينة")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "محاكاة فرع واحد" })).toBeNull();
    expect(screen.queryByRole("button", { name: "محاكاة عدة فروع" })).toBeNull();
  });

  it("searches the branch API and skips branch selection when one branch is found", async () => {
    const user = userEvent.setup();
    const seen: BranchSetupInput[] = [];
    render(<Harness branches={PREVIEW_ONE_BRANCH} onProbe={(value) => seen.push(value)} />);
    await openSecondary(user);
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    expect(screen.queryByLabelText("الخادم / عنوان IP")).toBeNull();
    await user.click(screen.getByRole("button", { name: "البحث الآن" }));
    expect(seen).toHaveLength(1);
    expect(seen[0].secondaryMode).toBe("discover");
    expect(screen.getByText("✓ تم الاتصال بخادم XtraPOS")).toBeTruthy();
    expect(screen.getByText("الخادم: Shop1")).toBeTruthy();
    expect(screen.getByText("العنوان: localhost")).toBeTruthy();
    expect(screen.getByText("المنفذ: 5080")).toBeTruthy();
    expect(screen.queryByText(/password|username|كلمة المرور|اسم قاعدة البيانات/)).toBeNull();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "السماح بربط هذا الجهاز" })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "اختيار الفرع" })).toBeNull();
  });

  it("opens branch selection when discovery returns several branches", async () => {
    const user = userEvent.setup();
    render(<Harness branches={PREVIEW_MANY_BRANCHES} />);
    await openSecondary(user);
    expect(screen.getByText("الخطوة 2")).toBeTruthy();
    expect(screen.queryByText(/من \d/)).toBeNull();
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    await user.click(screen.getByRole("button", { name: "البحث الآن" }));
    expect(screen.getByText("الخادم: خادم الفرع")).toBeTruthy();
    expect(screen.getByText("الخطوة 2 من 8")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "اختيار الفرع" })).toBeTruthy();
    expect(screen.getByText("Shop1")).toBeTruthy();
    expect(screen.getByText("Shop2")).toBeTruthy();
    expect(screen.getByText("المخزن")).toBeTruthy();
  });

  it("uses host and port on the manual path", async () => {
    const user = userEvent.setup();
    const seen: BranchSetupInput[] = [];
    render(<Harness onProbe={(value) => seen.push(value)} />);
    await openSecondary(user);
    await user.click(screen.getByText("إدخال عنوان الخادم يدويًا"));
    expect(screen.queryByRole("button", { name: "البحث الآن" })).toBeNull();
    expect(screen.queryByRole("button", { name: "محاكاة نجاح الاتصال" })).toBeNull();
    expect(screen.getByLabelText("الخادم / عنوان IP")).toBeTruthy();
    expect(screen.getByLabelText("المنفذ")).toBeTruthy();
    expect(screen.queryByLabelText("اسم المستخدم")).toBeNull();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "192.168.0.10");
    await user.clear(screen.getByLabelText("المنفذ"));
    await user.type(screen.getByLabelText("المنفذ"), "5080");
    await user.click(screen.getByRole("button", { name: "اختبار الاتصال" }));
    expect(seen[0].secondaryHost).toBe("192.168.0.10");
    expect(seen[0].secondaryPort).toBe("5080");
    expect(seen[0].secondaryMode).toBe("manual");
    expect(screen.getByText("العنوان: 192.168.0.10")).toBeTruthy();
    expect(screen.getByText("المنفذ: 5080")).toBeTruthy();
    expect(screen.queryByText("XtraPOS_Branch")).toBeNull();
  });

  it("simulates the manual path in preview without calling the branch API", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(() => {
      throw new Error("preview called the network");
    });
    vi.stubGlobal("fetch", fetchMock);
    const probe = vi.fn();
    render(<Harness preview onProbe={probe} />);
    await openSecondary(user);
    await user.click(screen.getByText("إدخال عنوان الخادم يدويًا"));
    expect(screen.queryByRole("button", { name: "اختبار الاتصال" })).toBeNull();
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "10.0.0.8");
    await user.click(screen.getByRole("button", { name: "خادم يعيد فرعًا واحدًا" }));
    expect(screen.getByText("العنوان: 10.0.0.8")).toBeTruthy();
    expect(screen.getByText("المنفذ: 5080")).toBeTruthy();
    expect(probe).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
