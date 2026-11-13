import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyBranchSetup } from "../types/setup";
import { visibleSteps } from "../setup/wizardFlow";
import { SetupWizard } from "./SetupWizard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("branch agent local setup waiting progress", () => {
  it("shows progress indicator while waiting for installer and pairing until SQL instances appear", async () => {
    let phase: "missing" | "unpaired" | "ready" = "missing";
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith("/downloads/branch-agent.json")) {
        return new Response(JSON.stringify({ version: "0.3.0" }), { status: 200 });
      }
      if (url.endsWith("/status")) {
        if (phase === "missing") {
          return new Response("offline", { status: 503 });
        }
        if (phase === "unpaired") {
          return new Response(JSON.stringify({
            product: "XtraPOS.BranchAgent",
            version: "0.3.0",
            machineName: "BRANCH-PC",
            paired: false,
            server: window.location.origin,
            agentId: null,
            instances: ["localhost", "localhost\\BARTENDER"],
            sqlAvailable: true,
          }), { status: 200 });
        }
        return new Response(JSON.stringify({
          product: "XtraPOS.BranchAgent",
          version: "0.3.0",
          machineName: "BRANCH-PC",
          paired: true,
          server: window.location.origin,
          agentId: "agent-123",
          instances: ["localhost", "localhost\\BARTENDER"],
          sqlAvailable: true,
        }), { status: 200 });
      }
      if (url.endsWith("/api/setup/branch-agents/pairing-codes")) {
        return new Response(JSON.stringify({ code: "ABCD1234" }), { status: 200 });
      }
      if (url.endsWith("/pair")) {
        phase = "ready";
        return new Response(JSON.stringify({
          product: "XtraPOS.BranchAgent",
          version: "0.3.0",
          machineName: "BRANCH-PC",
          paired: true,
          server: window.location.origin,
          agentId: "agent-123",
          instances: ["localhost", "localhost\\BARTENDER"],
          sqlAvailable: true,
        }), { status: 200 });
      }
      return new Response("{}", { status: 404 });
    });
    vi.stubGlobal("fetch", fetchMock);

    const draft = {
      ...emptyBranchSetup,
      deviceRole: "MainBranch" as const,
      roleLocked: true,
      operatingMode: "Standalone" as const,
      branchIntent: "new" as const,
      branchLocation: "Local" as const,
      branchDatabaseName: "XtraPOS_Branch_03",
    };
    const steps = visibleSteps(draft);
    const branchConnectionIndex = steps.indexOf("branchConnection");

    render(
      <SetupWizard
        value={draft}
        initialIndex={branchConnectionIndex}
        onChange={() => undefined}
        onCancel={() => undefined}
        onSubmit={() => undefined}
      />,
    );

    const installButton = await screen.findByRole("button", { name: "تثبيت خدمة XtraPOS على هذا الجهاز" });
    await waitFor(() => {
      expect(screen.queryByRole("progressbar")).toBeNull();
    });

    await userEvent.click(installButton);
    expect(screen.getByRole("progressbar")).toBeTruthy();
    expect(screen.getByText("بانتظار تثبيت وتشغيل خدمة XtraPOS على هذا الجهاز...")).toBeTruthy();

    phase = "unpaired";
    await waitFor(
      () => {
        expect(screen.getByRole("button", { name: "localhost" })).toBeTruthy();
        expect(screen.getByRole("button", { name: "localhost\\BARTENDER" })).toBeTruthy();
      },
      { timeout: 5000 },
    );
    expect(screen.queryByRole("progressbar")).toBeNull();
  });
});
