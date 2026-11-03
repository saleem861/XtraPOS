export type PasskeyStage =
  | "preflight"
  | "registration-options"
  | "assertion-options"
  | "credentials.create"
  | "credentials.get"
  | "submission";

export type PasskeyDiagnostic = {
  code: string;
  stage: PasskeyStage;
  exceptionName: string;
  message: string;
};

export type PasskeyEnvironment = {
  isSecureContext: boolean;
  hasPublicKeyCredential: boolean;
  hasCreate: boolean;
  hasGet: boolean;
  platformAuthenticatorAvailable: () => Promise<boolean>;
};

export function phonePasskeyCreation(input: {
  challenge: BufferSource;
  rpId: string;
  userId: BufferSource;
  userName: string;
  displayName: string;
}): PublicKeyCredentialCreationOptions {
  return {
    challenge: input.challenge,
    rp: { name: "XtraPOS", id: input.rpId },
    user: { id: input.userId, name: input.userName, displayName: input.displayName },
    pubKeyCredParams: [{ type: "public-key", alg: -7 }],
    authenticatorSelection: {
      authenticatorAttachment: "platform",
      residentKey: "required",
      requireResidentKey: true,
      userVerification: "required",
    },
    timeout: 60000,
    attestation: "none",
  };
}

export function readPasskeyEnvironment(): PasskeyEnvironment {
  const credential = typeof PublicKeyCredential === "undefined" ? undefined : PublicKeyCredential;
  return {
    isSecureContext: typeof window !== "undefined" && window.isSecureContext === true,
    hasPublicKeyCredential: credential !== undefined,
    hasCreate: typeof navigator !== "undefined" && typeof navigator.credentials?.create === "function",
    hasGet: typeof navigator !== "undefined" && typeof navigator.credentials?.get === "function",
    platformAuthenticatorAvailable: async () => credential?.isUserVerifyingPlatformAuthenticatorAvailable() ?? false,
  };
}

export async function inspectPhonePasskeyCapability(environment: PasskeyEnvironment): Promise<PasskeyDiagnostic | null> {
  if (!environment.isSecureContext) {
    return diagnostic("PASSKEY_INSECURE_CONTEXT", "preflight", "", "يجب فتح الصفحة عبر HTTPS حتى يمكن إنشاء Passkey.");
  }
  if (!environment.hasPublicKeyCredential || !environment.hasCreate || !environment.hasGet) {
    return diagnostic("PASSKEY_UNSUPPORTED_BROWSER", "preflight", "", "هذا المتصفح لا يدعم Passkey.");
  }
  try {
    const available = await environment.platformAuthenticatorAvailable();
    if (!available) {
      return diagnostic(
        "PASSKEY_PLATFORM_UNAVAILABLE",
        "preflight",
        "",
        "هذا الهاتف لا يوفر Passkey على الجهاز نفسه. لن يُستخدم مفتاح خارجي بدلاً منه.",
      );
    }
  } catch (error) {
    return classifyPasskeyError(error, "preflight");
  }
  return null;
}

export function classifyPasskeyError(error: unknown, stage: PasskeyStage): PasskeyDiagnostic {
  const exceptionName = error instanceof DOMException
    ? error.name
    : error instanceof TypeError
      ? "TypeError"
      : "";
  if (exceptionName === "TypeError" && (stage === "registration-options" || stage === "assertion-options" || stage === "submission")) {
    return diagnostic("PASSKEY_NETWORK_ERROR", stage, exceptionName, "تعذر الاتصال بالخادم. لم يكتمل التحقق.");
  }
  switch (exceptionName) {
    case "NotSupportedError":
      return diagnostic("PASSKEY_PLATFORM_UNAVAILABLE", stage, exceptionName, "هذا الجهاز لا يوفر وسيلة Passkey متوافقة على الهاتف نفسه.");
    case "SecurityError":
      return diagnostic("PASSKEY_INVALID_OPTIONS", stage, exceptionName, "رفض المتصفح إعدادات Passkey لأن عنوان الصفحة غير مقبول.");
    case "NotAllowedError":
      return diagnostic(
        "PASSKEY_BROWSER_NOT_ALLOWED",
        stage,
        exceptionName,
        "رفض المتصفح طلب Passkey. هذا لا يعني بالضرورة أنك ألغيت العملية؛ قد يكون السبب الإلغاء، أو غياب قفل الشاشة، أو منع المتصفح للطلب.",
      );
    case "AbortError":
      return diagnostic("PASSKEY_TIMEOUT", stage, exceptionName, "انتهى وقت طلب Passkey قبل اكتماله.");
    case "InvalidStateError":
      return diagnostic("PASSKEY_CREDENTIAL_ALREADY_REGISTERED", stage, exceptionName, "يوجد Passkey مسجل مسبقاً لهذا الحساب على الجهاز.");
    case "TypeError":
      return diagnostic("PASSKEY_INVALID_OPTIONS", stage, exceptionName, "تعذر تجهيز طلب Passkey على هذا المتصفح.");
    default:
      return diagnostic("PASSKEY_UNKNOWN_ERROR", stage, exceptionName, "فشل التحقق على هذا الجهاز.");
  }
}

export function formatPasskeyDiagnostic(value: PasskeyDiagnostic): string {
  const exception = value.exceptionName ? ` / ${value.exceptionName}` : "";
  return `${value.message} [${value.code} / ${value.stage}${exception}]`;
}

function diagnostic(code: string, stage: PasskeyStage, exceptionName: string, message: string): PasskeyDiagnostic {
  return { code, stage, exceptionName, message };
}
