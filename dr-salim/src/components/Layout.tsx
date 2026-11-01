import { useEffect, useState, type ReactNode } from "react";
import { useCart } from "../cart/CartContext.tsx";
import { house } from "../data/house.ts";
import { Link, useRouter } from "../router.tsx";

const links = [
  { to: "/collection", label: "Collection" },
  { to: "/about", label: "The house" },
  { to: "/contact", label: "Contact" },
] as const;

export function Layout({ children }: { children: ReactNode }) {
  const { count } = useCart();
  const { path } = useRouter();
  const [open, setOpen] = useState(false);

  useEffect(() => {
    setOpen(false);
  }, [path]);

  const cartLabel = count === 0 ? "Cart" : `Cart, ${count} ${count === 1 ? "item" : "items"}`;

  return (
    <div className="site">
      <a className="skip" href="#content">
        Skip to content
      </a>
      <header className="site-header">
        <div className="shell bar">
          <Link to="/" className="wordmark">
            <strong>Dr. Salim</strong>
            <span>Mutrah</span>
          </Link>
          <button
            type="button"
            className="nav-toggle"
            aria-expanded={open}
            aria-controls="site-nav"
            onClick={() => setOpen((value) => !value)}
          >
            {open ? "Close" : "Menu"}
          </button>
          <nav id="site-nav" className={open ? "site-nav is-open" : "site-nav"} aria-label="Primary">
            {links.map((link) => (
              <Link key={link.to} to={link.to} onClick={() => setOpen(false)}>
                {link.label}
              </Link>
            ))}
            <Link to="/cart" className="cart-link" ariaLabel={cartLabel} onClick={() => setOpen(false)}>
              Cart
              {count > 0 ? <span className="cart-count">{count}</span> : null}
            </Link>
          </nav>
        </div>
      </header>
      <main id="content" className="shell page">
        {children}
      </main>
      <footer className="site-footer">
        <div className="shell footer-grid">
          <div>
            <p className="footer-mark">Dr. Salim</p>
            <p className="fine-print">
              Perfume, written like a prescription.
              <br />
              {house.street}, {house.city}
            </p>
          </div>
          <nav className="footer-nav" aria-label="Footer">
            {links.map((link) => (
              <Link key={link.to} to={link.to}>
                {link.label}
              </Link>
            ))}
            <Link to="/cart">Cart</Link>
          </nav>
          <p className="fine-print">
            © {new Date().getFullYear()} Dr. Salim
            <br />
            <a href={`mailto:${house.email}`}>{house.email}</a>
          </p>
        </div>
      </footer>
    </div>
  );
}
