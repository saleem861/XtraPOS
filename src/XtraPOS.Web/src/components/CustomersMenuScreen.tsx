import { ChartNoAxesCombined, ContactRound, UserRoundPlus, WalletCards } from "lucide-react";
import { useState } from "react";
import type { CurrentUser, DeviceSession } from "../types/setup";
import type { CustomerListItem } from "../api/customers";
import { CustomerBrowseScreen } from "./CustomerBrowseScreen";
import { CustomerSearchScreen } from "./CustomerSearchScreen";
import { NewCustomerScreen } from "./NewCustomerScreen";
import { PosCollectionScreen } from "./PosCollectionScreen";
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
  const [view, setView] = useState<"menu" | "add" | "browse" | "debts">("menu");
  const [notice, setNotice] = useState("");
  const [debtSearch, setDebtSearch] = useState(false);
  const [debtCustomer, setDebtCustomer] = useState<CustomerListItem | null>(null);
  const [debtPickStamp, setDebtPickStamp] = useState(0);
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

  if (view === "debts") {
    return (
      <>
        <PosCollectionScreen session={session} initialCustomerId={null} initialNumber={null} initialName={null} cashSessionId={null} requireShift={false} picked={debtCustomer} pickStamp={debtPickStamp} onSearch={() => setDebtSearch(true)} onBack={() => setView("menu")} />
        {debtSearch ? (
          <CustomerSearchScreen
            session={session}
            permissions={user.permissions}
            onClose={() => setDebtSearch(false)}
            onChoose={(customer) => { setDebtCustomer(customer); setDebtPickStamp((value) => value + 1); setDebtSearch(false); }}
          />
        ) : null}
      </>
    );
  }

  return (
    <section className="card customers-menu">
      <ScreenHeader title="إدارة الزبائن" onBack={onBack} />
      {notice ? <p className="new-customer-success">{notice}</p> : null}
      <div className="customers-menu-grid">
        {cards.map((card) => {
          const Icon = card.icon;
          const active = card.title === "إضافة زبون جديد" ? canCreate : card.title === "استعراض الزبائن" || card.title === "ديون الزبائن" ? canView : false;
          return (
            <button
              key={card.title}
              type="button"
              className={`customers-menu-card customers-menu-card-${card.tone}`}
              disabled={!active}
              aria-disabled={!active}
              onClick={active ? () => { setNotice(""); setView(card.title === "إضافة زبون جديد" ? "add" : card.title === "ديون الزبائن" ? "debts" : "browse"); } : undefined}
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
