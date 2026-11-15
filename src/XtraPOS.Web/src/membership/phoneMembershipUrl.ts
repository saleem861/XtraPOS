export function phoneMembershipUrl(candidate: string | null | undefined) {
  const value = (candidate ?? "").trim();
  if (!value) return "";
  try {
    const host = new URL(value).hostname.replace(/^\[|\]$/g, "").toLowerCase();
    if (host === "localhost" || host === "::1" || host === "127.0.0.1" || host.startsWith("127.")) return "";
  } catch {
    return "";
  }
  return value.replace(/\/$/, "");
}
