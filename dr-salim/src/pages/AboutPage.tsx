import { house } from "../data/house.ts";
import { Link } from "../router.tsx";
import { usePageTitle } from "../usePageTitle.ts";

export function AboutPage() {
  usePageTitle("The house · Dr. Salim");

  return (
    <article className="prose">
      <header className="page-intro">
        <div>
          <p className="eyebrow">Est. {house.founded}</p>
          <h1>The house</h1>
        </div>
        <p className="lede">A clinic, then a laboratory. The same room above Mutrah.</p>
      </header>
      <div className="facts">
        <p className="fact">
          <strong>{house.founded}</strong>
          The clinic closed and the ledger was renumbered.
        </p>
        <p className="fact">
          <strong>House 14</strong>
          {house.street}. The shutters still face the corniche.
        </p>
        <p className="fact">
          <strong>{house.batch}</strong>
          Bottles in a batch. When they are gone, the formula rests.
        </p>
      </div>
      <p>
        {house.founder} trained as a physician and kept a small clinic for the port. The work was fever, stitches, and
        the ordinary troubles of a lane that never quite slept. In the evenings he compounded what the day required.
        Orange peel. Clove. Alcohol. Resin when a chest needed warming.
      </p>
      <p>
        He closed the clinic in {house.founded}. He did not throw the ledger away. Each perfume is still a formula: a
        concentration, a volume, a short list of materials, and a reason to wear it through a particular hour.
      </p>
      <p>
        The house does not launch seasons of flankers. Eight formulas are in the cabinet this year. Visitors are welcome{" "}
        {house.hours.toLowerCase()}, {house.visits.toLowerCase()}.
      </p>
      <p className="signoff">— {house.founder}</p>
      <div className="actions">
        <Link to="/collection" className="button solid">
          Enter the collection
        </Link>
        <Link to="/contact" className="button line">
          Write to the house
        </Link>
      </div>
    </article>
  );
}
