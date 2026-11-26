import type { Perfume } from "../data/perfumes.ts";
import { formatMoney } from "../format.ts";
import { Link } from "../router.tsx";
import { Bottle } from "./Bottle.tsx";

export function FormulaCard({ perfume, heading = "h2" }: { perfume: Perfume; heading?: "h2" | "h3" }) {
  const Title = heading;

  return (
    <Link to={`/collection/${perfume.slug}`} className="formula-card">
      <div className="bottle-frame">
        <Bottle glass={perfume.glass} cap={perfume.cap} formula={perfume.formula} />
      </div>
      <p className="formula-no">{perfume.formula}</p>
      <Title>{perfume.name}</Title>
      <p className="meta">
        {perfume.concentration} · {perfume.sizeMl} ml
      </p>
      <p className="summary">{perfume.summary}</p>
      <p className="price">{formatMoney(perfume.price)}</p>
    </Link>
  );
}
