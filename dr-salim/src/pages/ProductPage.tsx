import { useState } from "react";
import { MAX_QUANTITY } from "../cart/cart.ts";
import { useCart } from "../cart/CartContext.tsx";
import { perfumeBySlug } from "../data/perfumes.ts";
import { formatMoney } from "../format.ts";
import { Link } from "../router.tsx";
import { usePageTitle } from "../usePageTitle.ts";
import { Bottle } from "../components/Bottle.tsx";

export function ProductPage({ slug }: { slug: string }) {
  const perfume = perfumeBySlug(slug);
  usePageTitle(perfume ? `${perfume.name} · Dr. Salim` : "Formula not found · Dr. Salim");

  if (!perfume) {
    return (
      <div className="empty">
        <p className="eyebrow">Collection</p>
        <h1>This formula is not in the cabinet.</h1>
        <Link to="/collection" className="button solid">
          Back to the collection
        </Link>
      </div>
    );
  }

  return <ProductDetail slug={perfume.slug} />;
}

function ProductDetail({ slug }: { slug: string }) {
  const perfume = perfumeBySlug(slug);
  const { add, lines } = useCart();
  const [quantity, setQuantity] = useState(1);
  const [added, setAdded] = useState(false);

  if (!perfume) return null;

  const inCart = lines.find((line) => line.slug === perfume.slug)?.quantity ?? 0;

  return (
    <article className="product">
      <div className="product-stage">
        <div className="bottle-frame hero-bottle">
          <Bottle glass={perfume.glass} cap={perfume.cap} formula={perfume.formula} />
        </div>
      </div>
      <div className="product-copy">
        <p className="crumb">
          <Link to="/collection">Collection</Link>
          <span aria-hidden="true"> / </span>
          <span>{perfume.formula}</span>
        </p>
        <p className="formula-no">{perfume.formula}</p>
        <h1>{perfume.name}</h1>
        <p className="meta">
          {perfume.concentration} · {perfume.sizeMl} ml
        </p>
        <p className="product-price">{formatMoney(perfume.price)}</p>
        <p>{perfume.story}</p>
        <div className="pyramid">
          <div>
            <h2>Top</h2>
            <p>{perfume.top.join(" · ")}</p>
          </div>
          <div>
            <h2>Heart</h2>
            <p>{perfume.heart.join(" · ")}</p>
          </div>
          <div>
            <h2>Base</h2>
            <p>{perfume.base.join(" · ")}</p>
          </div>
        </div>
        <div className="actions">
          <div className="qty">
            <button
              type="button"
              aria-label={`Decrease quantity of ${perfume.name}`}
              disabled={quantity <= 1}
              onClick={() => setQuantity((current) => Math.max(1, current - 1))}
            >
              −
            </button>
            <span>{quantity}</span>
            <button
              type="button"
              aria-label={`Increase quantity of ${perfume.name}`}
              disabled={quantity >= MAX_QUANTITY}
              onClick={() => setQuantity((current) => Math.min(MAX_QUANTITY, current + 1))}
            >
              +
            </button>
          </div>
          <button
            type="button"
            className="button solid"
            onClick={() => {
              add(perfume.slug, quantity);
              setAdded(true);
            }}
          >
            Add to cart
          </button>
        </div>
        {inCart > 0 ? (
          <p className="fine-print">
            Already in your cart: {inCart}. {MAX_QUANTITY} is the most of one formula we will set aside.
          </p>
        ) : (
          <p className="fine-print">Up to {MAX_QUANTITY} of one formula. Payment is arranged with the atelier, not here.</p>
        )}
        {added ? (
          <p role="status" className="added">
            Added to your cart. <Link to="/cart">Review cart</Link>
          </p>
        ) : null}
      </div>
    </article>
  );
}
