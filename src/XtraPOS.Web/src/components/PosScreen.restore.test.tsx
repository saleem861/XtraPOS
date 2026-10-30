import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { addFixedLine, createCart } from "../cart/model";
import { DEVICE_TYPE_POS, type CurrentUser, type DeviceSession } from "../types/setup";
import { PosScreen } from "./PosScreen";

const { loadDraftMock } = vi.hoisted(() => ({ loadDraftMock: vi.fn() }));

vi.mock("../cart/draftStore", () => ({
  createIndexedDbDraftDatabase: () => ({
    get: async () => null,
    put: async () => undefined,
    remove: async () => undefined,
  }),
  loadDraft: (...args: unknown[]) => loadDraftMock(...args),
  saveDraft: async () => undefined,
  recordCartStorageEvent: () => undefined,
  publishCartRestoreView: () => undefined,
}));

const session = {
  deviceId: "device",
  businessId: "business",
  branchId: "branch",
  stationId: "station",
  deviceName: "صندوق",
  deviceType: DEVICE_TYPE_POS,
  deviceToken: "token",
  registeredAt: "2026-10-22",
  businessName: "Smoker World",
  branchName: "فرع سوق الذهب",
  branchType: "Retail",
  stationName: "كاونتر 1",
  stationNumber: 1,
} satisfies DeviceSession;

const user = {
  userId: "cashier",
  username: "cashier",
  displayName: "سليم",
  roles: [],
  permissions: [],
  businessName: "Smoker World",
  branchName: "فرع سوق الذهب",
  stationName: "كاونتر 1",
  deviceName: "صندوق",
} satisfies CurrentUser;

beforeEach(() => {
  loadDraftMock.mockReset();
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (String(url).includes("/health")) return new Response("ok");
    if (String(url).includes("/api/pos/cash-session/current")) {
      return new Response(JSON.stringify({ session: null }), { headers: { "Content-Type": "application/json" } });
    }
    return new Response("missing", { status: 404 });
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it("shows the selling screen when no saved draft exists", async () => {
  loadDraftMock.mockResolvedValue(null);
  render(<PosScreen session={session} user={user} onBack={() => undefined} />);
  expect(await screen.findByLabelText("رقم الصنف")).toBeTruthy();
  expect(screen.queryByText("جاري استعادة السلة…")).toBeNull();
  expect(screen.queryByText(/تعذر الوصول إلى التخزين المحلي/)).toBeNull();
});

it("restores a saved draft and leaves the loading state", async () => {
  const saved = addFixedLine(createCart({
    businessId: session.businessId,
    branchId: session.branchId,
    stationId: session.stationId,
    deviceId: session.deviceId,
    userId: user.userId,
  }), {
    itemId: "item",
    itemNumber: "650",
    itemName: "صنف محفوظ",
    priceTypeId: "price",
    priceTypeCode: "Price0",
    basePrice: "10",
    unitPrice: "10",
    priceSource: "CentralDefault",
    pricingResolvedAt: "2026-10-22T00:00:00Z",
  });
  loadDraftMock.mockResolvedValue(saved);
  render(<PosScreen session={session} user={user} onBack={() => undefined} />);
  expect(await screen.findByText("صنف محفوظ")).toBeTruthy();
  expect(screen.queryByText("جاري استعادة السلة…")).toBeNull();
});

it("shows a temporary cart warning when storage fails", async () => {
  loadDraftMock.mockRejectedValue(new Error("idb"));
  render(<PosScreen session={session} user={user} onBack={() => undefined} />);
  expect(await screen.findByText(/تعذر الوصول إلى التخزين المحلي/)).toBeTruthy();
  expect(screen.getByLabelText("رقم الصنف")).toBeTruthy();
  expect(screen.queryByText("جاري استعادة السلة…")).toBeNull();
});

it("creates the empty cart when randomUUID throws on an insecure page", async () => {
  const randomUuid = crypto.randomUUID.bind(crypto);
  crypto.randomUUID = () => {
    throw new Error("insecure");
  };
  loadDraftMock.mockResolvedValue(null);
  try {
    render(<PosScreen session={session} user={user} onBack={() => undefined} />);
    expect(await screen.findByLabelText("رقم الصنف")).toBeTruthy();
    expect(screen.queryByText("جاري استعادة السلة…")).toBeNull();
  } finally {
    crypto.randomUUID = randomUuid;
  }
});

it("restores a saved draft when StrictMode runs the effect twice", async () => {
  const saved = createCart({
    businessId: session.businessId,
    branchId: session.branchId,
    stationId: session.stationId,
    deviceId: session.deviceId,
    userId: user.userId,
  }, "saved-cart");
  loadDraftMock.mockResolvedValue({ ...saved, lines: [{ ...addFixedLine(saved, {
    itemId: "item",
    itemNumber: "650",
    itemName: "صنف محفوظ",
    priceTypeId: "price",
    priceTypeCode: "Price0",
    basePrice: "10",
    unitPrice: "10",
    priceSource: "CentralDefault",
    pricingResolvedAt: "2026-10-22T00:00:00Z",
  }).lines[0] }] });
  render(
    <StrictMode>
      <PosScreen session={session} user={user} onBack={() => undefined} />
    </StrictMode>,
  );
  expect(await screen.findByText("صنف محفوظ")).toBeTruthy();
  expect(screen.queryByText("جاري استعادة السلة…")).toBeNull();
});

it("reaches the selling screen when restore runs twice in StrictMode", async () => {
  loadDraftMock.mockResolvedValue(null);
  render(
    <StrictMode>
      <PosScreen session={session} user={user} onBack={() => undefined} />
    </StrictMode>,
  );
  await waitFor(() => expect(screen.queryByText("جاري استعادة السلة…")).toBeNull());
  expect(screen.getByLabelText("رقم الصنف")).toBeTruthy();
});
