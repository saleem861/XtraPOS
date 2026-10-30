import { useLayoutEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";
import { Package, Settings, ShoppingCart, UsersRound } from "lucide-react";
import { deviceHeaders } from "../api/client";
import { createItemClient } from "../api/items";
import { clearPersistedDevice } from "../storage/deviceConfig";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { CustomersMenuScreen } from "./CustomersMenuScreen";
import { CentralSetupScreen } from "./CentralSetupScreen";
import { ItemCatalogScreen } from "./ItemCatalogScreen";
import { PosScreen } from "./PosScreen";
import { UserManagementScreen } from "./UserManagementScreen";

interface HomeScreenProps {
  session: DeviceSession;
  user: CurrentUser;
  pending: boolean;
  onLogout: () => void;
  onReset: () => void;
}

export function HomeScreen({ session, user, pending, onLogout, onReset }: HomeScreenProps) {
  const [showItems, setShowItems] = useState(false);
  const [showPos, setShowPos] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [showUsers, setShowUsers] = useState(false);
  const [showCentral, setShowCentral] = useState(false);
  const [showCustomers, setShowCustomers] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);
  const [resetError, setResetError] = useState("");
  const [deregisterCentral, setDeregisterCentral] = useState(false);
  const [deleteDatabase, setDeleteDatabase] = useState(false);
  const client = useMemo(() => createItemClient(session), [session]);
  const roleNames = user.roles.map((role) => role.roleName).join("، ");
  const isAdmin = user.permissions.includes("System.Administer");
  const canViewItems = isAdmin || user.permissions.includes("Items.View");
  const canViewUsers = isAdmin || user.permissions.includes("Users.View");

  async function resetDevice() {
    setResetError("");
    const response = await fetch("/api/device/reset", {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({
        deregisterCentral,
        deleteExclusiveDatabase: deleteDatabase,
        exclusiveLocal: false,
        backupVerified: false,
        unsyncedFinancial: false,
        databaseName: "",
      }),
    });
    if (!response.ok) {
      setResetError("تعذر إعادة تهيئة هذا الجهاز.");
      return;
    }
    await clearPersistedDevice();
    onReset();
  }

  if (showPos && canViewItems) {
    return <PosScreen session={session} user={user} onBack={() => setShowPos(false)} onLogout={onLogout} />;
  }

  if (showItems && canViewItems) {
    return <ItemCatalogScreen client={client} onHome={() => setShowItems(false)} />;
  }

  if (showUsers && canViewUsers) {
    return (
      <UserManagementScreen
        session={session}
        onHome={() => {
          setShowUsers(false);
          setShowSettings(false);
        }}
        onBack={() => setShowUsers(false)}
      />
    );
  }

  if (showCentral && isAdmin) {
    return <CentralSetupScreen session={session} onHome={() => setShowCentral(false)} />;
  }

  if (showCustomers) {
    return <CustomersMenuScreen session={session} user={user} onBack={() => setShowCustomers(false)} />;
  }

  return (
    <section className="card home-menu">
      <HomeBar user={user} />
      <dl className="home-facts">
        <div>
          <dt>المنشأة</dt>
          <dd>{user.businessName}</dd>
        </div>
        <div>
          <dt>الفرع</dt>
          <dd>{user.branchName}</dd>
        </div>
        <div>
          <dt>الكاونتر</dt>
          <dd>{user.stationName}</dd>
        </div>
        <div>
          <dt>الجهاز</dt>
          <dd>{user.deviceName}</dd>
        </div>
        <div>
          <dt>المستخدم</dt>
          <dd>{user.displayName}</dd>
        </div>
        <div>
          <dt>الدور / الأدوار</dt>
          <dd>{roleNames || "—"}</dd>
        </div>
      </dl>
      <div className="home-nav">
        {canViewItems ? (
          <button className="home-nav-card" type="button" onClick={() => setShowPos(true)}>
            <span className="home-nav-icon" aria-hidden="true"><ShoppingCart /></span>
            <span><strong>نقطة البيع</strong></span>
          </button>
        ) : null}
        {canViewItems ? (
          <button className="home-nav-card" type="button" onClick={() => setShowItems(true)}>
            <span className="home-nav-icon" aria-hidden="true"><Package /></span>
            <span><strong>الأصناف</strong></span>
          </button>
        ) : null}
        <button className="home-nav-card" type="button" onClick={() => setShowCustomers(true)}>
          <span className="home-nav-icon" aria-hidden="true"><UsersRound /></span>
          <span>
            <strong>الزبائن</strong>
            <small>إدارة الزبائن والحسابات</small>
          </span>
        </button>
        {isAdmin || canViewUsers ? (
          <button className="home-nav-card" type="button" onClick={() => setShowSettings((open) => !open)}>
            <span className="home-nav-icon" aria-hidden="true"><Settings /></span>
            <span><strong>الإعدادات</strong></span>
          </button>
        ) : null}
      </div>
      {showSettings ? (
        <div className="settings-menu">
          {canViewUsers ? (
            <button className="nav-button" type="button" onClick={() => setShowUsers(true)}>
              إدارة المستخدمين
            </button>
          ) : null}
          {isAdmin ? (
            <button className="nav-button" type="button" onClick={() => setShowCentral(true)}>
              الربط المركزي
            </button>
          ) : null}
          {isAdmin ? (
            <button className="nav-button" type="button" onClick={() => { setResetError(""); setDeregisterCentral(false); setDeleteDatabase(false); setResetOpen(true); }}>
              إعادة تهيئة النقطة
            </button>
          ) : null}
        </div>
      ) : null}
      {resetOpen ? (
        <form className="form" onSubmit={(event) => { event.preventDefault(); void resetDevice(); }}>
          <p>ستُمسح تهيئة هذه النقطة فقط. قاعدة الفرع المشتركة وXtraPOS_Central وقاعدة المقاطعة لا تُحذف.</p>
          <label className="check-line">
            <input type="checkbox" checked={deregisterCentral} onChange={(event) => setDeregisterCentral(event.target.checked)} />
            <span>إلغاء تسجيل النقطة في المركزية</span>
          </label>
          <label className="check-line">
            <input type="checkbox" checked={deleteDatabase} onChange={(event) => setDeleteDatabase(event.target.checked)} />
            <span>حذف قاعدة البيانات المحلية الحصرية أيضًا</span>
          </label>
          <p>حذف القاعدة يُرفض ما لم يثبت الخادم أنها حصرية لهذا الجهاز وبعد نسخة احتياطية. الخيار غير محدد افتراضياً.</p>
          <label className="check-line">
            <input type="checkbox" required />
            <span>أؤكد إعادة تهيئة هذا الجهاز</span>
          </label>
          {resetError ? <p className="form-error">{resetError}</p> : null}
          <button className="primary-button" type="submit">تأكيد إعادة التهيئة</button>
          <button className="ghost-button" type="button" onClick={() => setResetOpen(false)}>إلغاء</button>
        </form>
      ) : null}
      <button className="primary-button" type="button" onClick={onLogout} disabled={pending}>
        تسجيل الخروج
      </button>
    </section>
  );
}

function HomeBar({ user }: { user: CurrentUser }) {
  const [topbarSlot, setTopbarSlot] = useState<HTMLElement | null>(null);
  useLayoutEffect(() => {
    setTopbarSlot(document.getElementById("pos-topbar-slot"));
  }, []);
  if (!topbarSlot) {
    return null;
  }

  return createPortal(
    <div className="home-topbar">
      <h1>الرئيسية</h1>
      <span>{`${user.businessName} · ${user.branchName} · ${user.displayName}`}</span>
    </div>,
    topbarSlot,
  );
}
