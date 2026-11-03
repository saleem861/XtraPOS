import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MembershipPhonePage } from "./MembershipPhonePage";

describe("MembershipPhonePage", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    document.cookie = "xtrapos-membership=; xtrapos-membership-csrf=; Max-Age=0";
  });

  it("verifies automatically when a session exists and does not ask for confirmation", async () => {
    document.cookie = "xtrapos-membership=session; xtrapos-membership-csrf=csrf";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      if (String(url).endsWith("/complete")) {
        expect(init?.method).toBe("POST");
        return { ok: true, json: async () => ({ status: "Verified" }) };
      }
      return { ok: true, json: async () => ({ status: "Pending" }) };
    }));
    render(<MembershipPhonePage pathname="/membership/verify/opaque-token" />);
    expect(screen.queryByRole("button", { name: "تأكيد" })).toBeNull();
    await waitFor(() => expect(screen.getByText("تم التعرف على عضويتك بنجاح")).toBeTruthy());
  });

  it("shows provider login when the server has no membership session", async () => {
    document.cookie = "xtrapos-membership-csrf=csrf";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      if (String(url).endsWith("/complete")) {
        return { ok: false, json: async () => ({ code: "MEMBERSHIP_SESSION_REQUIRED" }) };
      }
      return { ok: true, json: async () => ({ status: "Pending" }) };
    }));
    render(<MembershipPhonePage pathname="/membership/verify/opaque-token" />);
    await waitFor(() => expect(screen.getByText("يلزم تسجيل الدخول إلى العضوية.")).toBeTruthy());
    expect(screen.getByRole("link", { name: "Google" })).toBeTruthy();
    expect(screen.queryByText("تم التعرف على عضويتك بنجاح")).toBeNull();
  });
});
