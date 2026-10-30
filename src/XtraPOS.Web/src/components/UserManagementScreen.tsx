import { useEffect, useMemo, useState } from "react";
import { createUserClient, type ManagedUser, type UserAdminCatalog, type UserDraft, type UserQuery } from "../api/users";
import type { DeviceSession } from "../types/setup";
import { ScreenHeader } from "./ScreenHeader";

interface UserManagementScreenProps {
  session: DeviceSession;
  onHome: () => void;
  onBack: () => void;
}

interface Secret {
  username: string;
  fullName: string;
  password: string;
}

const emptyQuery: UserQuery = { search: "", active: "", role: "", branchId: "" };

function emptyDraft(catalog: UserAdminCatalog | null): UserDraft {
  return {
    fullName: "",
    employeeNumber: "",
    username: "",
    roleCode: "User",
    branchIds: catalog?.branches.map((branch) => branch.branchId) ?? [],
    isActive: true,
    passwordMode: "auto",
    password: "",
  };
}

function formatLogin(value: string | null) {
  if (!value) {
    return "—";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "—";
  }
  return new Intl.DateTimeFormat("ar", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

export function TemporaryPasswordDialog({ secret, onClose }: { secret: Secret; onClose: () => void }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(secret.password);
    setCopied(true);
  }

  return (
    <div className="modal-backdrop" role="presentation">
      <div className="modal-card" role="dialog" aria-modal="true" aria-labelledby="temp-password-title">
        <h2 id="temp-password-title">تم إنشاء كلمة مرور مؤقتة</h2>
        <p>تظهر كلمة المرور هذه مرة واحدة. بعد الإغلاق لا يمكن استرجاعها.</p>
        <dl className="details">
          <div>
            <dt>اسم المستخدم</dt>
            <dd>{secret.username}</dd>
          </div>
          <div>
            <dt>اسم الموظف</dt>
            <dd>{secret.fullName}</dd>
          </div>
          <div>
            <dt>كلمة المرور المؤقتة</dt>
            <dd className="secret-password">{secret.password}</dd>
          </div>
        </dl>
        <div className="action-row">
          <button className="nav-button" type="button" onClick={() => void copy()}>
            {copied ? "تم النسخ" : "نسخ كلمة المرور"}
          </button>
          <button className="primary-button" type="button" onClick={onClose}>
            إغلاق
          </button>
        </div>
      </div>
    </div>
  );
}

export function UserManagementScreen({ session, onBack }: UserManagementScreenProps) {
  const client = useMemo(() => createUserClient(session), [session]);
  const [catalog, setCatalog] = useState<UserAdminCatalog | null>(null);
  const [query, setQuery] = useState<UserQuery>(emptyQuery);
  const [applied, setApplied] = useState<UserQuery>(emptyQuery);
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [count, setCount] = useState(0);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState("");
  const [panel, setPanel] = useState<"create" | "edit" | "role" | "branches" | "reset" | "view" | null>(null);
  const [selected, setSelected] = useState<ManagedUser | null>(null);
  const [draft, setDraft] = useState<UserDraft>(emptyDraft(null));
  const [secret, setSecret] = useState<Secret | null>(null);
  const [confirmedReset, setConfirmedReset] = useState(false);

  async function load(next = applied) {
    setPending(true);
    setMessage("");
    try {
      const [nextCatalog, page] = await Promise.all([client.catalog(), client.list(next)]);
      setCatalog(nextCatalog);
      setUsers(page.users);
      setCount(page.count);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر عرض المستخدمين.");
    } finally {
      setPending(false);
    }
  }

  useEffect(() => {
    let cancelled = false;
    const handle = window.setTimeout(() => {
      void (async () => {
        setPending(true);
        setMessage("");
        try {
          const [nextCatalog, page] = await Promise.all([client.catalog(), client.list(emptyQuery)]);
          if (!cancelled) {
            setCatalog(nextCatalog);
            setUsers(page.users);
            setCount(page.count);
          }
        } catch (error) {
          if (!cancelled) {
            setMessage(error instanceof Error ? error.message : "تعذر عرض المستخدمين.");
          }
        } finally {
          if (!cancelled) {
            setPending(false);
          }
        }
      })();
    }, 0);
    return () => {
      cancelled = true;
      window.clearTimeout(handle);
    };
  }, [client]);

  function openCreate() {
    setSelected(null);
    setDraft(emptyDraft(catalog));
    setPanel("create");
    setMessage("");
  }

  function openUser(user: ManagedUser, next: "view" | "edit" | "role" | "branches" | "reset") {
    setSelected(user);
    setDraft({
      fullName: user.fullName,
      employeeNumber: user.employeeNumber ?? "",
      username: user.username,
      roleCode: user.roleCode ?? "User",
      branchIds: user.branches.map((branch) => branch.branchId),
      isActive: user.isActive,
      passwordMode: "auto",
      password: "",
    });
    setConfirmedReset(false);
    setPanel(next);
    setMessage("");
  }

  async function submitCreate() {
    setPending(true);
    setMessage("");
    try {
      const result = await client.create(draft);
      setPanel(null);
      setSecret({
        username: result.user?.username ?? draft.username,
        fullName: result.user?.fullName ?? draft.fullName,
        password: result.temporaryPassword,
      });
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر إضافة المستخدم.");
    } finally {
      setPending(false);
    }
  }

  async function submitEdit() {
    if (!selected) {
      return;
    }
    setPending(true);
    try {
      await client.update(selected.userId, draft);
      setPanel(null);
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر تعديل المستخدم.");
    } finally {
      setPending(false);
    }
  }

  async function submitActive(user: ManagedUser) {
    setPending(true);
    try {
      await client.setActive(user.userId, !user.isActive);
      setPanel(null);
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر تغيير حالة المستخدم.");
    } finally {
      setPending(false);
    }
  }

  async function submitRole() {
    if (!selected) {
      return;
    }
    setPending(true);
    try {
      await client.setRole(selected.userId, draft.roleCode);
      setPanel(null);
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر تغيير الدور.");
    } finally {
      setPending(false);
    }
  }

  async function submitBranches() {
    if (!selected) {
      return;
    }
    setPending(true);
    try {
      await client.setBranches(selected.userId, draft.branchIds);
      setPanel(null);
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر تغيير الفروع.");
    } finally {
      setPending(false);
    }
  }

  async function submitReset() {
    if (!selected || !confirmedReset) {
      setMessage("يلزم تأكيد المسؤول قبل إعادة التعيين.");
      return;
    }
    setPending(true);
    try {
      const result = await client.resetPassword(selected.userId, draft.passwordMode, draft.password);
      setPanel(null);
      setConfirmedReset(false);
      setSecret({
        username: result.user?.username ?? selected.username,
        fullName: result.user?.fullName ?? selected.fullName,
        password: result.temporaryPassword,
      });
      await load();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "تعذر إعادة تعيين كلمة المرور.");
    } finally {
      setPending(false);
    }
  }

  function toggleBranch(branchId: string) {
    setDraft((current) => ({
      ...current,
      branchIds: current.branchIds.includes(branchId)
        ? current.branchIds.filter((id) => id !== branchId)
        : [...current.branchIds, branchId],
    }));
  }

  const canManage = catalog?.canManage === true;
  const canReset = catalog?.canReset === true;

  return (
    <section className="card user-screen">
      <ScreenHeader title="إدارة المستخدمين" onBack={onBack} />
      <p className="form-notice">
        {catalog?.authority === "Central"
          ? "الحفظ يتم في الإدارة المركزية، ثم تُحدَّث نسخة هذا الفرع."
          : "هذا الفرع يدير المستخدمين محلياً."}
      </p>
      <form
        className="user-filters"
        onSubmit={(event) => {
          event.preventDefault();
          setApplied(query);
          void load(query);
        }}
      >
        <input
          aria-label="بحث"
          placeholder="بحث بالاسم أو رقم الموظف أو اسم المستخدم"
          value={query.search}
          onChange={(event) => setQuery({ ...query, search: event.target.value })}
        />
        <select aria-label="الحالة" value={query.active} onChange={(event) => setQuery({ ...query, active: event.target.value as UserQuery["active"] })}>
          <option value="">كل الحالات</option>
          <option value="active">فعال</option>
          <option value="inactive">غير فعال</option>
        </select>
        <select aria-label="الدور" value={query.role} onChange={(event) => setQuery({ ...query, role: event.target.value })}>
          <option value="">كل الأدوار</option>
          {catalog?.roles.map((role) => (
            <option key={role.roleCode} value={role.roleCode}>{role.roleName}</option>
          ))}
        </select>
        <select aria-label="الفرع" value={query.branchId} onChange={(event) => setQuery({ ...query, branchId: event.target.value })}>
          <option value="">كل الفروع</option>
          {catalog?.branches.map((branch) => (
            <option key={branch.branchId} value={branch.branchId}>{branch.branchName}</option>
          ))}
        </select>
        <button className="nav-button" type="submit" disabled={pending}>بحث</button>
      </form>
      <div className="user-toolbar">
        <strong>{count} مستخدم</strong>
        {canManage ? (
          <button className="nav-button" type="button" onClick={openCreate}>إضافة مستخدم</button>
        ) : null}
      </div>
      {message ? <p className="form-error" role="alert">{message}</p> : null}
      <div className="user-scroll">
        <div className="user-table-wrap">
          <table className="user-table">
            <thead>
              <tr>
                <th>الاسم</th>
                <th>رقم الموظف</th>
                <th>اسم المستخدم</th>
                <th>الدور</th>
                <th>الحالة</th>
                <th>الفروع المسموحة</th>
                <th>يحتاج تغيير كلمة المرور؟</th>
                <th>آخر دخول</th>
                <th>إجراءات</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user.userId}>
                  <td>{user.fullName}</td>
                  <td>{user.employeeNumber || "—"}</td>
                  <td>{user.username}</td>
                  <td>{user.roleName || "—"}</td>
                  <td>{user.isActive ? "فعال" : "غير فعال"}</td>
                  <td>{user.branches.map((branch) => branch.branchName).join("، ") || "—"}</td>
                  <td>{user.mustChangePassword ? "نعم" : "لا"}</td>
                  <td>{formatLogin(user.lastLoginAt)}</td>
                  <td><UserActions user={user} canManage={canManage} canReset={canReset} pending={pending} onOpen={openUser} onActive={(item) => void submitActive(item)} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="user-cards">
          {users.map((user) => (
            <article className="user-card" key={user.userId}>
              <h2>{user.fullName}</h2>
              <dl>
                <div><dt>رقم الموظف</dt><dd>{user.employeeNumber || "—"}</dd></div>
                <div><dt>اسم المستخدم</dt><dd>{user.username}</dd></div>
                <div><dt>الدور</dt><dd>{user.roleName || "—"}</dd></div>
                <div><dt>الحالة</dt><dd>{user.isActive ? "فعال" : "غير فعال"}</dd></div>
                <div><dt>الفروع المسموحة</dt><dd>{user.branches.map((branch) => branch.branchName).join("، ") || "—"}</dd></div>
                <div><dt>يحتاج تغيير كلمة المرور؟</dt><dd>{user.mustChangePassword ? "نعم" : "لا"}</dd></div>
                <div><dt>آخر دخول</dt><dd>{formatLogin(user.lastLoginAt)}</dd></div>
              </dl>
              <UserActions user={user} canManage={canManage} canReset={canReset} pending={pending} onOpen={openUser} onActive={(item) => void submitActive(item)} />
            </article>
          ))}
        </div>
      </div>
      {panel ? (
        <UserPanel
          panel={panel}
          draft={draft}
          catalog={catalog}
          selected={selected}
          pending={pending}
          confirmedReset={confirmedReset}
          onDraft={setDraft}
          onToggleBranch={toggleBranch}
          onConfirmReset={setConfirmedReset}
          onClose={() => setPanel(null)}
          onCreate={() => void submitCreate()}
          onEdit={() => void submitEdit()}
          onRole={() => void submitRole()}
          onBranches={() => void submitBranches()}
          onReset={() => void submitReset()}
        />
      ) : null}
      {secret ? <TemporaryPasswordDialog secret={secret} onClose={() => setSecret(null)} /> : null}
    </section>
  );
}

function UserActions({
  user,
  canManage,
  canReset,
  pending,
  onOpen,
  onActive,
}: {
  user: ManagedUser;
  canManage: boolean;
  canReset: boolean;
  pending: boolean;
  onOpen: (user: ManagedUser, panel: "view" | "edit" | "role" | "branches" | "reset") => void;
  onActive: (user: ManagedUser) => void;
}) {
  return (
    <div className="action-row">
      <button className="nav-button" type="button" onClick={() => onOpen(user, "view")}>عرض</button>
      {canManage ? <button className="nav-button" type="button" onClick={() => onOpen(user, "edit")}>تعديل</button> : null}
      {canManage ? (
        <button className="nav-button" type="button" disabled={pending} onClick={() => onActive(user)}>
          {user.isActive ? "إيقاف" : "تفعيل"}
        </button>
      ) : null}
      {canReset ? <button className="nav-button" type="button" onClick={() => onOpen(user, "reset")}>إعادة تعيين كلمة المرور</button> : null}
      {canManage ? <button className="nav-button" type="button" onClick={() => onOpen(user, "branches")}>إدارة الفروع</button> : null}
      {canManage ? <button className="nav-button" type="button" onClick={() => onOpen(user, "role")}>إدارة الدور</button> : null}
    </div>
  );
}

function UserPanel({
  panel,
  draft,
  catalog,
  selected,
  pending,
  confirmedReset,
  onDraft,
  onToggleBranch,
  onConfirmReset,
  onClose,
  onCreate,
  onEdit,
  onRole,
  onBranches,
  onReset,
}: {
  panel: "create" | "edit" | "role" | "branches" | "reset" | "view";
  draft: UserDraft;
  catalog: UserAdminCatalog | null;
  selected: ManagedUser | null;
  pending: boolean;
  confirmedReset: boolean;
  onDraft: (draft: UserDraft) => void;
  onToggleBranch: (branchId: string) => void;
  onConfirmReset: (value: boolean) => void;
  onClose: () => void;
  onCreate: () => void;
  onEdit: () => void;
  onRole: () => void;
  onBranches: () => void;
  onReset: () => void;
}) {
  const title = panel === "create" ? "إضافة مستخدم"
    : panel === "edit" ? "تعديل مستخدم"
      : panel === "role" ? "إدارة الدور"
        : panel === "branches" ? "إدارة الفروع"
          : panel === "reset" ? "إعادة تعيين كلمة المرور"
            : "عرض المستخدم";

  return (
    <div className="modal-backdrop" role="presentation">
      <div className="modal-card" role="dialog" aria-modal="true" aria-labelledby="user-panel-title">
        <h2 id="user-panel-title">{title}</h2>
        {panel === "view" && selected ? (
          <dl className="details">
            <div><dt>الاسم</dt><dd>{selected.fullName}</dd></div>
            <div><dt>رقم الموظف</dt><dd>{selected.employeeNumber || "—"}</dd></div>
            <div><dt>اسم المستخدم</dt><dd>{selected.username}</dd></div>
            <div><dt>الدور</dt><dd>{selected.roleName || "—"}</dd></div>
            <div><dt>الحالة</dt><dd>{selected.isActive ? "فعال" : "غير فعال"}</dd></div>
            <div><dt>الفروع المسموحة</dt><dd>{selected.branches.map((branch) => branch.branchName).join("، ") || "—"}</dd></div>
            <div><dt>يحتاج تغيير كلمة المرور؟</dt><dd>{selected.mustChangePassword ? "نعم" : "لا"}</dd></div>
            <div><dt>آخر دخول</dt><dd>{formatLogin(selected.lastLoginAt)}</dd></div>
          </dl>
        ) : null}
        {panel === "create" || panel === "edit" ? (
          <div className="user-form">
            <label>الاسم الكامل<input value={draft.fullName} onChange={(event) => onDraft({ ...draft, fullName: event.target.value })} /></label>
            <label>رقم الموظف<input value={draft.employeeNumber} onChange={(event) => onDraft({ ...draft, employeeNumber: event.target.value })} /></label>
            <label>اسم المستخدم<input value={draft.username} onChange={(event) => onDraft({ ...draft, username: event.target.value })} /></label>
            {panel === "create" ? (
              <>
                <label>الدور
                  <select value={draft.roleCode} onChange={(event) => onDraft({ ...draft, roleCode: event.target.value })}>
                    {catalog?.roles.map((role) => <option key={role.roleCode} value={role.roleCode}>{role.roleName}</option>)}
                  </select>
                </label>
                <fieldset>
                  <legend>الفروع المسموح بها</legend>
                  {catalog?.branches.map((branch) => (
                    <label className="check-line" key={branch.branchId}>
                      <input type="checkbox" checked={draft.branchIds.includes(branch.branchId)} onChange={() => onToggleBranch(branch.branchId)} />
                      {branch.branchName}
                    </label>
                  ))}
                </fieldset>
                <label className="check-line">
                  <input type="checkbox" checked={draft.isActive} onChange={(event) => onDraft({ ...draft, isActive: event.target.checked })} />
                  فعال
                </label>
                <fieldset>
                  <legend>كلمة المرور المؤقتة</legend>
                  <label className="check-line">
                    <input type="radio" name="password-mode" checked={draft.passwordMode === "auto"} onChange={() => onDraft({ ...draft, passwordMode: "auto", password: "" })} />
                    إنشاء كلمة مرور مؤقتة تلقائياً
                  </label>
                  <label className="check-line">
                    <input type="radio" name="password-mode" checked={draft.passwordMode === "manual"} onChange={() => onDraft({ ...draft, passwordMode: "manual" })} />
                    إدخال كلمة مرور مؤقتة يدوياً
                  </label>
                  {draft.passwordMode === "manual" ? (
                    <input aria-label="كلمة المرور المؤقتة" type="password" value={draft.password} onChange={(event) => onDraft({ ...draft, password: event.target.value })} />
                  ) : null}
                </fieldset>
              </>
            ) : null}
          </div>
        ) : null}
        {panel === "role" ? (
          <label>الدور
            <select value={draft.roleCode} onChange={(event) => onDraft({ ...draft, roleCode: event.target.value })}>
              {catalog?.roles.map((role) => <option key={role.roleCode} value={role.roleCode}>{role.roleName}</option>)}
            </select>
          </label>
        ) : null}
        {panel === "branches" ? (
          <fieldset>
            <legend>الفروع المسموح بها</legend>
            {catalog?.branches.map((branch) => (
              <label className="check-line" key={branch.branchId}>
                <input type="checkbox" checked={draft.branchIds.includes(branch.branchId)} onChange={() => onToggleBranch(branch.branchId)} />
                {branch.branchName}
              </label>
            ))}
          </fieldset>
        ) : null}
        {panel === "reset" ? (
          <div className="user-form">
            <p>لن يتغير الدور أو الفروع أو حالة التفعيل. سيُطلب من المستخدم تغيير كلمة المرور عند الدخول.</p>
            <label className="check-line">
              <input type="radio" name="reset-mode" checked={draft.passwordMode === "auto"} onChange={() => onDraft({ ...draft, passwordMode: "auto", password: "" })} />
              إنشاء كلمة مرور مؤقتة تلقائياً
            </label>
            <label className="check-line">
              <input type="radio" name="reset-mode" checked={draft.passwordMode === "manual"} onChange={() => onDraft({ ...draft, passwordMode: "manual" })} />
              إدخال كلمة مرور مؤقتة يدوياً
            </label>
            {draft.passwordMode === "manual" ? (
              <input aria-label="كلمة المرور المؤقتة" type="password" value={draft.password} onChange={(event) => onDraft({ ...draft, password: event.target.value })} />
            ) : null}
            <label className="check-line">
              <input type="checkbox" checked={confirmedReset} onChange={(event) => onConfirmReset(event.target.checked)} />
              أؤكد إعادة تعيين كلمة المرور
            </label>
          </div>
        ) : null}
        <div className="action-row">
          {panel === "create" ? <button className="primary-button" type="button" disabled={pending} onClick={onCreate}>حفظ</button> : null}
          {panel === "edit" ? <button className="primary-button" type="button" disabled={pending} onClick={onEdit}>حفظ</button> : null}
          {panel === "role" ? <button className="primary-button" type="button" disabled={pending} onClick={onRole}>حفظ الدور</button> : null}
          {panel === "branches" ? <button className="primary-button" type="button" disabled={pending} onClick={onBranches}>حفظ الفروع</button> : null}
          {panel === "reset" ? <button className="primary-button" type="button" disabled={pending || !confirmedReset} onClick={onReset}>إعادة التعيين</button> : null}
          <button className="nav-button" type="button" onClick={onClose}>إغلاق</button>
        </div>
      </div>
    </div>
  );
}
