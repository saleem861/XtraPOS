import { Link } from "../router.tsx";
import { usePageTitle } from "../usePageTitle.ts";

export function NotFoundPage() {
  usePageTitle("Page not found · Dr. Salim");

  return (
    <div className="empty">
      <p className="eyebrow">404</p>
      <h1>This page is not in the ledger.</h1>
      <Link to="/" className="button solid">
        Return home
      </Link>
    </div>
  );
}
