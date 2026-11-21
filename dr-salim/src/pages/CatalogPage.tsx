import { useState } from "react";
import { concentrationFilters, perfumes, type ConcentrationFilter } from "../data/perfumes.ts";
import { usePageTitle } from "../usePageTitle.ts";
import { FormulaCard } from "../components/FormulaCard.tsx";

export function CatalogPage() {
  usePageTitle("The collection · Dr. Salim");
  const [filter, setFilter] = useState<ConcentrationFilter>("All");
  const shown = filter === "All" ? perfumes : perfumes.filter((perfume) => perfume.concentration === filter);

  return (
    <>
      <header className="page-intro">
        <div>
          <p className="eyebrow">Eight formulas</p>
          <h1>The collection</h1>
        </div>
        <p className="lede">
          Each bottle is a concentration, a volume, and a short list of materials. Prices are in US dollars. The atelier
          confirms a request before anything is filled.
        </p>
      </header>
      <div className="filters" role="group" aria-label="Concentration">
        {concentrationFilters.map((option) => (
          <button
            key={option}
            type="button"
            className="chip"
            aria-pressed={filter === option}
            onClick={() => setFilter(option)}
          >
            {option}
          </button>
        ))}
      </div>
      <p className="result-count">
        {shown.length} {shown.length === 1 ? "formula" : "formulas"}
      </p>
      <div className="catalog-grid">
        {shown.map((perfume) => (
          <FormulaCard key={perfume.slug} perfume={perfume} />
        ))}
      </div>
    </>
  );
}
