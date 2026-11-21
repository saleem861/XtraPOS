import { MAX_QUANTITY, cartSubtotal } from "../cart/cart.ts";
import { useCart } from "../cart/CartContext.tsx";
import { perfumeBySlug } from "../data/perfumes.ts";
import { formatMoney } from "../format.ts";
import { Link } from "../router.tsx";
import { usePageTitle } from "../usePageTitle.ts";
import { Bottle } from "../components/Bottle.tsx";

export function CartPage() {
  usePageTitle("Cart · Dr. Salim");
  const { lines, setQuantity, remove } = useCart();
  const subtotal = cartSubtotal(lines, (slug) => perfumeBySlug(slug)?.price);

  return (
    <>
      <header className="page-intro">
        <div>
          <p className="eyebrow">Selection</p>
          <h1>Your cart</h1>
        </div>
        <p className="lede">
          Review the formulas you want set aside. The house confirms availability before anything is filled. This page
          does not take payment.
        </p>
      </header>
      {lines.length === 0 ? (
        <div className="empty">
          <p>Your cart is empty.</p>
          <Link to="/collection" className="button solid">
            Browse the collection
          </Link>
        </div>
      ) : (
        <>
          <ul className="cart-list">
            {lines.map((line) => {
              const perfume = perfumeBySlug(line.slug);
              const name = perfume?.name ?? line.slug;
              return (
                <li key={line.slug} className="cart-line">
                  <div className="bottle-frame tiny">
                    {perfume ? <Bottle glass={perfume.glass} cap={perfume.cap} formula={perfume.formula} /> : null}
                  </div>
                  <div>
                    <p className="formula-no">{perfume?.formula ?? "Retired"}</p>
                    <h2>{name}</h2>
                    {perfume ? (
                      <p className="meta">
                        {perfume.concentration} · {perfume.sizeMl} ml · {formatMoney(perfume.price)}
                      </p>
                    ) : (
                      <p className="meta">This formula has left the cabinet.</p>
                    )}
                    <div className="line-actions">
                      <div className="qty">
                        <button
                          type="button"
                          aria-label={`Decrease quantity of ${name}`}
                          disabled={line.quantity <= 1}
                          onClick={() => setQuantity(line.slug, line.quantity - 1)}
                        >
                          −
                        </button>
                        <span>{line.quantity}</span>
                        <button
                          type="button"
                          aria-label={`Increase quantity of ${name}`}
                          disabled={line.quantity >= MAX_QUANTITY}
                          onClick={() => setQuantity(line.slug, line.quantity + 1)}
                        >
                          +
                        </button>
                      </div>
                      <button type="button" className="text-button" onClick={() => remove(line.slug)}>
                        Remove {name}
                      </button>
                    </div>
                  </div>
                  <p className="line-total">{formatMoney((perfume?.price ?? 0) * line.quantity)}</p>
                </li>
              );
            })}
          </ul>
          <p className="totals">
            <span>Subtotal</span>
            <strong>{formatMoney(subtotal)}</strong>
          </p>
          <p className="fine-print">
            A request is not a purchase. Write to the atelier with this selection and they will reply with what can be
            filled.
          </p>
          <div className="actions">
            <Link to="/contact?selection=1" className="button solid">
              Write to the atelier
            </Link>
            <Link to="/collection" className="button line">
              Continue browsing
            </Link>
          </div>
        </>
      )}
    </>
  );
}
