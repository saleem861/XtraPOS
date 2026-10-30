export type ItemActivity = "active" | "inactive" | "all";

export type ItemSort = "number" | "name";

export interface ItemQuery {
  search: string;
  activity: ItemActivity;
  page: number;
  pageSize: number;
  sort: ItemSort;
  resolvePrice?: boolean;
  customerId?: string | null;
}

export interface ItemListItem {
  itemId: string;
  itemNumber: string;
  itemName: string;
  itemType?: string | null;
  manufacturer?: string | null;
  classification?: string | null;
  isActive: boolean;
  isProhibited: boolean;
  resolvedPrice?: number | null;
}

export interface ItemPage {
  items: ItemListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ItemDetail {
  itemId: string;
  itemNumber: string;
  itemName: string;
  itemType?: string | null;
  manufacturer?: string | null;
  color?: string | null;
  taste?: string | null;
  size?: string | null;
  packing?: string | null;
  classification?: string | null;
  countryOfOrigin?: string | null;
  productType?: string | null;
  isActive: boolean;
  isProhibited: boolean;
  isPublishedOnline: boolean;
  parentItemNumber?: string | null;
  parentItemName?: string | null;
}

export interface ItemPrice {
  priceCode: string;
  priceName: string;
  price?: number | null;
  defaultPrice?: number | null;
  branchPrice?: number | null;
}

export interface ItemUnit {
  unitsPerPackage: number;
  internalUnitItemNumber?: string | null;
  recordedUnitName?: string | null;
  currentItemName?: string | null;
}

export interface ItemUnits {
  units: ItemUnit[];
  ownsPhysicalStock: boolean;
  stockOwnerItemNumber: string;
  stockOwnerItemName: string;
  baseQuantityFactor: number;
}

export type QuantityState = "BranchPending" | "NotCounted" | "Known";

export interface ItemInventory {
  branchReady: boolean;
  isDerived: boolean;
  stockOwnerItemNumber?: string | null;
  stockOwnerItemName?: string | null;
  baseQuantityFactor: number;
  quantityState: QuantityState;
  onHandQuantity?: number | null;
}

export interface ItemMovement {
  occurredAt: string;
  movementName: string;
  quantityDelta: number;
  notes?: string | null;
  referenceType?: string | null;
}

export interface ItemWriteResult {
  rowVersion: string;
  changeVersion?: number | null;
}

export interface ItemEditPrice {
  priceCode: string;
  priceName: string;
  defaultPrice?: number | null;
  branchPrice?: number | null;
  defaultRowVersion?: string | null;
}

export interface ItemEditForm {
  topology: "Standalone" | "CentralLinked" | "Central";
  centralReachable: boolean;
  masterReadOnly: boolean;
  masterReadOnlyMessage?: string | null;
  canEditMaster: boolean;
  canEditBranchState: boolean;
  canEditBranchPrice: boolean;
  itemName: string;
  itemType?: string | null;
  manufacturer?: string | null;
  color?: string | null;
  taste?: string | null;
  size?: string | null;
  packing?: string | null;
  classification?: string | null;
  countryOfOrigin?: string | null;
  productType?: string | null;
  parentItemId?: string | null;
  isActive: boolean;
  isProhibited: boolean;
  isPublishedOnline: boolean;
  rowVersion?: string | null;
  unitCost?: number | null;
  isAvailable: boolean;
  branchActive: boolean;
  prices: ItemEditPrice[];
}

export interface ItemMasterInput {
  itemNumber?: string | null;
  itemName: string;
  itemType?: string | null;
  manufacturer?: string | null;
  color?: string | null;
  taste?: string | null;
  size?: string | null;
  packing?: string | null;
  classification?: string | null;
  countryOfOrigin?: string | null;
  productType?: string | null;
  parentItemId?: string | null;
  isActive: boolean;
  isProhibited: boolean;
  isPublishedOnline: boolean;
  rowVersion?: string | null;
}

export interface ItemCatalogClient {
  loadItems(query: ItemQuery): Promise<ItemPage>;
  loadDetail(itemId: string): Promise<ItemDetail>;
  loadPrices(itemId: string): Promise<ItemPrice[]>;
  loadUnits(itemId: string): Promise<ItemUnits>;
  loadInventory(itemId: string): Promise<ItemInventory>;
  loadMovements(itemId: string): Promise<ItemMovement[]>;
  loadEditForm(itemId: string): Promise<ItemEditForm>;
  saveMaster(itemId: string, body: ItemMasterInput): Promise<ItemWriteResult>;
  saveUnits(itemId: string, body: { rowVersion?: string | null; units: Array<{ itemUnitId?: string | null; unitsPerPackage: number; internalUnitItemId?: string | null; internalUnitItemNumber?: string | null; internalUnitItemName?: string | null; isActive: boolean }> }): Promise<ItemWriteResult>;
  saveDefaultPrice(itemId: string, body: { priceCode: string; price: number; rowVersion?: string | null }): Promise<ItemWriteResult>;
  saveBranchPrice(itemId: string, body: { priceCode: string; price?: number | null }): Promise<void>;
  saveBranchState(itemId: string, body: { unitCost?: number | null; isAvailable: boolean; isActive: boolean }): Promise<void>;
}
