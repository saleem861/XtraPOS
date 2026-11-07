const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function savedMembershipIdentity(value: string | null | undefined, subject?: string | null): string {
  const text = (value ?? "").trim();
  if (!text || guidPattern.test(text)) return "";
  const hiddenSubject = (subject ?? "").trim();
  if (hiddenSubject && text === hiddenSubject) return "";
  return text;
}
