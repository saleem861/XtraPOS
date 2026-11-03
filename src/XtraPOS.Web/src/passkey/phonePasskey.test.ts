import { describe, expect, it } from "vitest";
import {
  classifyPasskeyError,
  formatPasskeyDiagnostic,
  inspectPhonePasskeyCapability,
  phonePasskeyCreation,
  type PasskeyEnvironment,
} from "./phonePasskey";

const ready: PasskeyEnvironment = {
  isSecureContext: true,
  hasPublicKeyCredential: true,
  hasCreate: true,
  hasGet: true,
  platformAuthenticatorAvailable: async () => true,
};

describe("phone passkey registration", () => {
  it("asks the current phone for a discoverable passkey", () => {
    const options = phonePasskeyCreation({
      challenge: new Uint8Array([1]),
      rpId: "test-pos.sharbelfashion.com",
      userId: new Uint8Array([2]),
      userName: "059****707",
      displayName: "سليم",
    });

    expect(options.rp.id).toBe("test-pos.sharbelfashion.com");
    expect(options.pubKeyCredParams).toEqual([{ type: "public-key", alg: -7 }]);
    expect(options.authenticatorSelection).toEqual({
      authenticatorAttachment: "platform",
      residentKey: "required",
      requireResidentKey: true,
      userVerification: "required",
    });
    expect(options.attestation).toBe("none");
  });
});

describe("phone passkey capability", () => {
  it("rejects an insecure page before creating a credential", async () => {
    const failure = await inspectPhonePasskeyCapability({ ...ready, isSecureContext: false });
    expect(failure?.code).toBe("PASSKEY_INSECURE_CONTEXT");
    expect(failure?.stage).toBe("preflight");
  });

  it("rejects a browser without WebAuthn", async () => {
    const failure = await inspectPhonePasskeyCapability({ ...ready, hasPublicKeyCredential: false });
    expect(failure?.code).toBe("PASSKEY_UNSUPPORTED_BROWSER");
  });

  it("stops when the phone has no platform authenticator", async () => {
    const failure = await inspectPhonePasskeyCapability({
      ...ready,
      platformAuthenticatorAvailable: async () => false,
    });
    expect(failure?.code).toBe("PASSKEY_PLATFORM_UNAVAILABLE");
    expect(formatPasskeyDiagnostic(failure!)).toContain("لن يُستخدم مفتاح خارجي");
  });

  it("allows the ceremony when the phone authenticator is available", async () => {
    expect(await inspectPhonePasskeyCapability(ready)).toBeNull();
  });
});

describe("phone passkey error classification", () => {
  it.each([
    ["NotSupportedError", "PASSKEY_PLATFORM_UNAVAILABLE"],
    ["SecurityError", "PASSKEY_INVALID_OPTIONS"],
    ["NotAllowedError", "PASSKEY_BROWSER_NOT_ALLOWED"],
    ["AbortError", "PASSKEY_TIMEOUT"],
    ["InvalidStateError", "PASSKEY_CREDENTIAL_ALREADY_REGISTERED"],
  ])("maps %s during credential creation", (name, code) => {
    const failure = classifyPasskeyError(new DOMException("browser", name), "credentials.create");
    expect(failure.code).toBe(code);
    expect(failure.stage).toBe("credentials.create");
    expect(failure.exceptionName).toBe(name);
    expect(formatPasskeyDiagnostic(failure)).toContain(name);
  });

  it("does not describe NotAllowedError as a confirmed cancellation", () => {
    const failure = classifyPasskeyError(new DOMException("no", "NotAllowedError"), "credentials.create");
    expect(failure.message).toContain("لا يعني بالضرورة");
    expect(failure.code).not.toBe("PASSKEY_USER_CANCELLED");
  });

  it("maps TypeError during creation separately from a network failure", () => {
    expect(classifyPasskeyError(new TypeError("bad id"), "credentials.create").code).toBe("PASSKEY_INVALID_OPTIONS");
    expect(classifyPasskeyError(new TypeError("Failed to fetch"), "submission").code).toBe("PASSKEY_NETWORK_ERROR");
    expect(classifyPasskeyError(new TypeError("Failed to fetch"), "registration-options").stage).toBe("registration-options");
    expect(classifyPasskeyError(new TypeError("Failed to fetch"), "credentials.get").stage).toBe("credentials.get");
  });
});
