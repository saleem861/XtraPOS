export const concentrations = ["Extrait", "Eau de Parfum", "Eau de Toilette", "Eau de Cologne"] as const;

export type Concentration = (typeof concentrations)[number];

export const concentrationFilters = ["All", ...concentrations] as const;

export type ConcentrationFilter = (typeof concentrationFilters)[number];

export type Perfume = {
  slug: string;
  formula: string;
  name: string;
  concentration: Concentration;
  sizeMl: number;
  price: number;
  glass: string;
  cap: string;
  summary: string;
  story: string;
  top: readonly string[];
  heart: readonly string[];
  base: readonly string[];
  featured?: boolean;
};

export const perfumes: readonly Perfume[] = [
  {
    slug: "doctors-coat",
    formula: "No. 01",
    name: "Doctor's Coat",
    concentration: "Extrait",
    sizeMl: 30,
    price: 210,
    glass: "#e6e2d8",
    cap: "#8d9394",
    summary: "Starched linen, iris, and the clean edge of a coat after rounds.",
    story:
      "The first formula, written the year the clinic closed. It smells of pressed cotton, cold iris, and a trace of iodine soap that never quite left the cuffs.",
    top: ["Aldehydes", "Lemon peel"],
    heart: ["Iris", "Clean musk"],
    base: ["Starched linen", "White cedar"],
    featured: true,
  },
  {
    slug: "garden-after-rain",
    formula: "No. 02",
    name: "Garden After Rain",
    concentration: "Eau de Toilette",
    sizeMl: 100,
    price: 96,
    glass: "#7d9a62",
    cap: "#17352d",
    summary: "Wet leaves, jasmine, and the green of a courtyard just after rain.",
    story:
      "Mutrah keeps its rain in stone. This one is the courtyard behind the house: crushed leaves, jasmine opening late, and vetiver still damp at the root.",
    top: ["Green mandarin", "Petrichor"],
    heart: ["Jasmine sambac", "Violet leaf"],
    base: ["Vetiver", "Moss"],
  },
  {
    slug: "citrus-ward",
    formula: "No. 03",
    name: "Citrus Ward",
    concentration: "Eau de Cologne",
    sizeMl: 100,
    price: 78,
    glass: "#f0d78c",
    cap: "#f7f4ec",
    summary: "A bright ward of bergamot, neroli, and white musk.",
    story:
      "Splashed, not dabbed. The morning formula: bitter peel, orange blossom, and a musk quiet enough to wear in a warm room.",
    top: ["Bergamot", "Petitgrain"],
    heart: ["Neroli", "Orange blossom"],
    base: ["White musk", "Cedar"],
  },
  {
    slug: "resin-hour",
    formula: "No. 04",
    name: "Resin Hour",
    concentration: "Eau de Parfum",
    sizeMl: 75,
    price: 142,
    glass: "#8a5a2a",
    cap: "#2a2118",
    summary: "Frankincense, black tea, and the hour when the shutters close.",
    story:
      "Named for the late afternoon, when the laboratory smells of resin and tea. Frankincense leads, cedar keeps it dry, benzoin sweetens the close.",
    top: ["Black tea", "Bergamot"],
    heart: ["Frankincense", "Cedar"],
    base: ["Myrrh", "Benzoin"],
  },
  {
    slug: "night-clinic",
    formula: "No. 07",
    name: "Night Clinic",
    concentration: "Extrait",
    sizeMl: 50,
    price: 185,
    glass: "#1a2744",
    cap: "#c9a36a",
    summary: "Bitter orange over clove, oud, and a ribbon of smoke.",
    story:
      "The signature after dark. Orange peel cut against clove, then oud and labdanum, as if the lamps in the old clinic had just been put out.",
    top: ["Bitter orange", "Black pepper"],
    heart: ["Clove", "Rose absolute"],
    base: ["Oud", "Labdanum", "Smoke"],
    featured: true,
  },
  {
    slug: "saffron-study",
    formula: "No. 09",
    name: "Saffron Study",
    concentration: "Eau de Parfum",
    sizeMl: 50,
    price: 156,
    glass: "#8e2430",
    cap: "#c9842c",
    summary: "Saffron threads, rose, and a leather-bound desk.",
    story:
      "A study formula: saffron warmed over rose, then leather and sandalwood. It is the smell of a ledger left open under a lamp.",
    top: ["Saffron", "Bergamot"],
    heart: ["Rose", "Leather"],
    base: ["Sandalwood", "Amber"],
    featured: true,
  },
  {
    slug: "salt-ledger",
    formula: "No. 11",
    name: "Salt Ledger",
    concentration: "Eau de Parfum",
    sizeMl: 50,
    price: 128,
    glass: "#7f9c96",
    cap: "#d7d2c8",
    summary: "Sea salt, fig leaf, and driftwood from the corniche.",
    story:
      "The port comes upstairs. Salt on skin, a green fig leaf, and driftwood worn smooth. An ambergris accord keeps the dry-down mineral rather than sweet.",
    top: ["Sea salt", "Bergamot"],
    heart: ["Fig leaf", "Driftwood"],
    base: ["Ambergris accord", "Musk"],
  },
  {
    slug: "amber-prescription",
    formula: "No. 12",
    name: "Amber Prescription",
    concentration: "Extrait",
    sizeMl: 50,
    price: 198,
    glass: "#6b3a1f",
    cap: "#1c1915",
    summary: "Honey, tobacco leaf, benzoin, and a long amber close.",
    story:
      "The evening dose. Honey and tobacco leaf up front, then benzoin, tonka, and labdanum. Wear it when the day is already over.",
    top: ["Honey", "Tobacco leaf"],
    heart: ["Benzoin", "Vanilla"],
    base: ["Tonka", "Labdanum", "Cocoa"],
  },
];

const bySlug = new Map(perfumes.map((perfume) => [perfume.slug, perfume]));

export function perfumeBySlug(slug: string): Perfume | undefined {
  return bySlug.get(slug);
}
