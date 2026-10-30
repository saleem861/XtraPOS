const GENERIC_HOSTS = new Set([
  "",
  "localhost",
  "127.0.0.1",
  "windows",
  "win32",
  "linux",
  "macintel",
  "iphone",
  "ipad",
  "android",
]);

export function suggestDeviceName(hostLabel?: string | null): string {
  const label = (hostLabel ?? "").replace(/[\r\n]/g, " ").trim();
  if (GENERIC_HOSTS.has(label.toLowerCase())) {
    return "";
  }

  return label.slice(0, 80);
}

export function readHostDeviceLabel(): string {
  if (typeof navigator === "undefined") {
    return "";
  }

  const host = navigator as Navigator & { userAgentData?: { platform?: string } };
  return host.userAgentData?.platform ?? "";
}
