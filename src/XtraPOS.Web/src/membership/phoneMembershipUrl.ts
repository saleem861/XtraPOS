export const publicMembershipOrigin = "https://test-pos.sharbelfashion.com";

function localHost(hostname: string) {
  const host = hostname.replace(/^\[|\]$/g, "").toLowerCase();
  return host === "localhost" || host === "::1" || host === "127.0.0.1" || host.startsWith("127.");
}

export function phoneMembershipUrl(candidate: string | null | undefined) {
  const value = (candidate ?? "").trim();
  if (!value) return "";
  try {
    const url = new URL(value);
    if (url.protocol !== "http:" && url.protocol !== "https:") return "";
    if (!localHost(url.hostname)) return value.replace(/\/$/, "");
    if (!url.pathname.startsWith("/membership/")) return "";
    return `${publicMembershipOrigin}${url.pathname}${url.search}`.replace(/\/$/, "");
  } catch {
    return "";
  }
}
