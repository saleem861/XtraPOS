export interface MembershipDisplayMessage {
  type?: string;
  url?: string;
  status?: string;
  counterId?: string;
}

export function membershipDisplayUpdate(message: MembershipDisplayMessage, boundCounter?: string) {
  if (message.type !== "qr") return null;
  if (boundCounter && message.counterId !== boundCounter) return null;
  const status = message.status ?? "";
  return {
    status,
    url: status === "Pending" ? (message.url ?? "") : "",
  };
}
