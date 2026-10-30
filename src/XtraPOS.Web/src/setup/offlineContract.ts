export const OFFLINE_ACKNOWLEDGEMENT =
  "أفهم أن الكمية المعروضة أثناء عدم الاتصال هي آخر كمية متزامنة وقد لا تكون محدثة.";

export const OFFLINE_STOCK_NOTICE = "غير متصل — الكمية المعروضة قد لا تكون محدثة";
export const OFFLINE_CACHE_STORE = "IndexedDB";

export interface PendingSaleIdentity {
  saleId: string;
  deviceId: string;
  posStationId: string;
  createdAt: string;
  offlineCreated: true;
}

export function createPendingSale(deviceId: string, posStationId: string, createdAt: string, saleId = crypto.randomUUID()): PendingSaleIdentity {
  return {
    saleId,
    deviceId,
    posStationId,
    createdAt,
    offlineCreated: true,
  };
}

export function acceptPendingSales(existing: PendingSaleIdentity[], incoming: PendingSaleIdentity[]): PendingSaleIdentity[] {
  const seen = new Set(existing.map((sale) => sale.saleId));
  const accepted = [...existing];
  for (const sale of incoming) {
    if (seen.has(sale.saleId)) {
      continue;
    }
    seen.add(sale.saleId);
    accepted.push(sale);
  }
  return accepted;
}
