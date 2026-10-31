import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SyncStatusBadge } from "./SyncStatusBadge";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function statusResponse(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

describe("sync status badge", () => {
  it("omits the badge when the branch has no central connection", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => statusResponse({
      state: "Standalone",
      operation: "pos-replica",
      detail: "لا يوجد اتصال محفوظ بالمركزية.",
    })));

    render(<SyncStatusBadge />);

    await vi.waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(screen.queryByRole("button", { name: "الاتصال غير متاح" })).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("omits the badge for the previous unlinked offline payload", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => statusResponse({
      state: "Offline",
      operation: "pos-replica",
      detail: "لا يوجد اتصال محفوظ بالمركزية.",
    })));

    render(<SyncStatusBadge />);

    await vi.waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("shows an error when a central replica sync fails", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => statusResponse({
      state: "Error",
      operation: "pos-replica",
      detail: "تعذر تحديث نسخة نقاط البيع.",
    })));

    render(<SyncStatusBadge />);

    expect(await screen.findByRole("button", { name: "خطأ في المزامنة" })).toBeTruthy();
  });

  it("shows offline when a saved central connection cannot be reached", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => statusResponse({
      state: "Offline",
      operation: "pos-suggest",
      detail: "المركزية غير متاحة",
    })));

    render(<SyncStatusBadge />);

    expect(await screen.findByRole("button", { name: "الاتصال غير متاح" })).toBeTruthy();
  });
});
