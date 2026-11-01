import { ChartNoAxesCombined, ContactRound, UserRoundPlus, WalletCards } from "lucide-react";
import { useState } from "react";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { CustomerBrowseScreen } from "./CustomerBrowseScreen";
import { NewCustomerScreen } from "./NewCustomerScreen";
import { ScreenHeader } from "./ScreenHeader";

interface CustomersMenuScreenProps {
  session: DeviceSession;
  user: CurrentUser;
  onBack: () => void;
}

const cards = [
  {
    title: "إضافة زبون جديد",
    description: "إنشاء ملف زبون جديد",
    tone: "green",
    icon: UserRoundPlus,
  },
  {
    title: "استعراض الزبائن",
    description: "بحث، عرض وتعديل بيانات الزبائن",
    tone: "blue",
    icon: ContactRound,
  },
  {
    title: "مشتريات الزبون",
    description: "عرض المشتريات، الأرباح وحالة التقدم أو التأخر",
    tone: "violet",
    icon: ChartNoAxesCombined,
  },
  {
    title: "ديون الزبائن",
    description: "الاستعلام عن الديون، التسديد والتعديل",
    tone: "amber",
    icon: WalletCards,
  },
] as const;

export function CustomersMenuScreen({ session, user, onBack }: CustomersMenuScreenProps) {
  const [view, setView] = useState<"menu" | "add" | "browse">("menu");
  const [notice, setNotice] = useState("");
  const canManage = user.permissions.includes("Customers.Manage") || user.permissions.includes("System.Administer");
  const canCreate = canManage || user.permissions.includes("Customers.Create");
  const canView = canCreate || user.permissions.includes("Customers.View");
  if (view === "add") {
    return (
      <NewCustomerScreen
        session={session}
        permissions={user.permissions}
        title="إضافة زبون جديد"
        onBack={() => setView("menu")}
        onSaved={() => {
          setNotice("تم حفظ الزبون.");
          setView("menu");
        }}
      />
    );
  }

  if (view === "browse") {
    return <CustomerBrowseScreen session={session} permissions={user.permissions} onBack={() => setView("menu")} />;
  }

  return (
    <section className="card customers-menu">
      <ScreenHeader title="إدارة الزبائن" onBack={onBack} />
      {notice ? <p className="new-customer-success">{notice}</p> : null}
      <div className="customers-menu-grid">
        {cards.map((card) => {
          const Icon = card.icon;
          const active = card.title === "إضافة زبون جديد" ? canCreate : card.title === "استعراض الزبائن" ? canView : false;
          return (
            <button
              key={card.title}
              type="button"
              className={`customers-menu-card customers-menu-card-${card.tone}`}
              disabled={!active}
              aria-disabled={!active}
              onClick={active ? () => { setNotice(""); setView(card.title === "إضافة زبون جديد" ? "add" : "browse"); } : undefined}
            >
              <span className="customers-menu-icon" aria-hidden="true">
                <Icon />
              </span>
              <span className="customers-menu-copy">
                <strong>{card.title}</strong>
                <small>{card.description}</small>
              </span>
            </button>
          );
        })}
      </div>
    </section>
  );
}
