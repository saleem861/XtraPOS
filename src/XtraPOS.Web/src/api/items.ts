import type { DeviceSession } from "../types/setup";
import { deviceHeaders, SetupClientError } from "./client";
import type {
  ItemCatalogClient,
  ItemDetail,
  ItemEditForm,
  ItemInventory,
  ItemMasterInput,
  ItemMovement,
  ItemPage,
  ItemPrice,
  ItemQuery,
  ItemUnits,
  ItemWriteResult,
} from "../catalog/types";

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (response.status === 401) {
    throw new SetupClientError("انتهت الجلسة. سجّل الدخول مرة أخرى.");
  }

  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { message?: string };
      if (body.message?.trim()) {
        message = body.message;
      }
    } catch {
      message = fallback;
    }
    if (import.meta.env.DEV) {
      console.error("Item request failed", response.status, response.url, message);
    }
    throw new SetupClientError(message);
  }

  return (await response.json()) as T;
}

function activityParam(activity: ItemQuery["activity"]): string {
  if (activity === "inactive") {
    return "false";
  }

  if (activity === "all") {
    return "all";
  }

  return "true";
}

export function createItemClient(session: DeviceSession): ItemCatalogClient {
  const headers = deviceHeaders(session);

  async function get<T>(path: string, fallback: string): Promise<T> {
    const response = await fetch(path, { credentials: "include", headers });
    return readJson<T>(response, fallback);
  }

  async function send<T>(path: string, method: string, body: unknown, fallback: string): Promise<T> {
    const response = await fetch(path, {
      method,
      credentials: "include",
      headers: { ...headers, "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    return readJson<T>(response, fallback);
  }

  return {
    async loadItems(query) {
      const params = new URLSearchParams({
        search: query.search,
        isActive: activityParam(query.activity),
        page: String(query.page),
        pageSize: String(query.pageSize),
        sort: query.sort,
      });
      if (query.resolvePrice) {
        params.set("resolvePrice", "true");
      }
      if (query.customerId) {
        params.set("customerId", query.customerId);
      }
      return get<ItemPage>(`/api/items?${params.toString()}`, "تعذر تحميل الأصناف.");
    },
    loadDetail: (itemId) => get<ItemDetail>(`/api/items/${itemId}`, "تعذر تحميل الصنف."),
    loadPrices: (itemId) => get<ItemPrice[]>(`/api/items/${itemId}/prices`, "تعذر تحميل الأسعار."),
    loadUnits: (itemId) => get<ItemUnits>(`/api/items/${itemId}/units`, "تعذر تحميل الوحدات."),
    loadInventory: (itemId) => get<ItemInventory>(`/api/items/${itemId}/inventory`, "تعذر تحميل المخزون."),
    loadMovements: (itemId) => get<ItemMovement[]>(`/api/items/${itemId}/stock-movements`, "تعذر تحميل الحركات."),
    loadEditForm: (itemId) => get<ItemEditForm>(`/api/items/${itemId}/edit-form`, "تعذر تحميل نموذج الصنف."),
    saveMaster: (itemId, body: ItemMasterInput) => send<ItemWriteResult>(`/api/items/${itemId}`, "PUT", body, "تعذر حفظ البيانات الأساسية."),
    saveUnits: (itemId, body) => send<ItemWriteResult>(`/api/items/${itemId}/units`, "PUT", body, "تعذر حفظ الوحدات."),
    saveDefaultPrice: (itemId, body) => send<ItemWriteResult>(`/api/items/${itemId}/prices/default`, "PUT", body, "تعذر حفظ السعر الأساسي."),
    saveBranchPrice: async (itemId, body) => {
      await send(`/api/items/${itemId}/prices/branch`, "PUT", body, "تعذر حفظ سعر هذا الفرع.");
    },
    saveBranchState: async (itemId, body) => {
      await send(`/api/items/${itemId}/branch-state`, "PUT", body, "تعذر حفظ بيانات الفرع.");
    },
  };
}
