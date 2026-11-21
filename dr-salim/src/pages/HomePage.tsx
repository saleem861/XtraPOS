import { perfumes } from "../data/perfumes.ts";
import { usePageTitle } from "../usePageTitle.ts";
import { Bottle } from "../components/Bottle.tsx";
import { FormulaCard } from "../components/FormulaCard.tsx";
import { Link } from "../router.tsx";

export function HomePage() {
  usePageTitle("Dr. Salim");
  const featured = perfumes.filter((perfume) => perfume.featured);

  return (
    <>
      <section className="hero">
        <div className="hero-copy">
          <p className="eyebrow">Formula house · Est. 1998</p>
          <h1>
            Written like{" "}
            <br />
            a prescription.
          </h1>
          <p className="lede">
            Dr. Salim numbers fragrance the way a physician numbers a prescription. Eight formulas, filled by hand in
            Mutrah, in batches of forty.
          </p>
          <div className="actions">
            <Link to="/collection" className="button paper">
              Enter the collection
            </Link>
            <Link to="/about" className="button line light">
              The house
            </Link>
          </div>
        </div>
        <div className="hero-still">
          <div className="bottle-frame back">
            <Bottle glass="#7f9c96" cap="#d7d2c8" formula="No. 11" />
          </div>
          <div className="bottle-frame front">
            <Bottle glass="#1a2744" cap="#c9a36a" formula="No. 07" />
          </div>
          <div className="stamp">
            <span>Batch of forty</span>
            <strong>Hand filled</strong>
          </div>
        </div>
      </section>

      <section className="section">
        <div className="section-head">
          <h2>In the cabinet</h2>
          <Link to="/collection">See the full collection</Link>
        </div>
        <div className="catalog-grid">
          {featured.map((perfume) => (
            <FormulaCard key={perfume.slug} perfume={perfume} heading="h3" />
          ))}
        </div>
      </section>

      <section className="section house-band">
        <blockquote className="pull">The examination table is gone. The shelves are not.</blockquote>
        <div>
          <p>
            Yusuf Salim kept a clinic on a lane above the corniche. Patients came for ordinary troubles. In the evenings
            he compounded tinctures, and the room smelled of orange peel, clove, and alcohol. When the clinic closed,
            he renumbered the ledger.
          </p>
          <Link to="/about" className="text-link">
            Read the house
          </Link>
        </div>
      </section>

      <section className="section">
        <h2>How a formula is written</h2>
        <div className="steps">
          <article className="step">
            <span>01</span>
            <h3>Observe</h3>
            <p>A formula starts with an hour of the day: morning rounds, rain in the courtyard, the study after dark.</p>
          </article>
          <article className="step">
            <span>02</span>
            <h3>Compose</h3>
            <p>Materials are written the way a tincture was written. Concentration, volume, and a reason to wear it.</p>
          </article>
          <article className="step">
            <span>03</span>
            <h3>Wear</h3>
            <p>Nothing is launched as a flanker. When a material runs out, the formula rests until it can be filled again.</p>
          </article>
        </div>
      </section>
    </>
  );
}
