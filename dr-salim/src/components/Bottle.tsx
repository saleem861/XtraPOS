import { useId } from "react";

type BottleProps = {
  glass: string;
  cap: string;
  formula: string;
};

export function Bottle({ glass, cap, formula }: BottleProps) {
  const gradientId = `glass-${useId().replace(/:/g, "")}`;

  return (
    <svg className="bottle" viewBox="0 0 140 240" aria-hidden="true">
      <defs>
        <linearGradient id={gradientId} x1="0" x2="1" y1="0" y2="1">
          <stop offset="0" stopColor="#ffffff" stopOpacity="0.55" />
          <stop offset="0.28" stopColor={glass} />
          <stop offset="1" stopColor={glass} />
        </linearGradient>
      </defs>
      <rect x="54" y="8" width="32" height="20" rx="3" fill={cap} />
      <rect x="48" y="26" width="44" height="10" rx="2" fill={cap} />
      <path d="M62 36h16l6 18H56z" fill={glass} />
      <path
        d="M40 60h60c7 12 14 22 14 36v104c0 16-12 24-26 24H52c-14 0-26-8-26-24V96c0-14 7-24 14-36z"
        fill={`url(#${gradientId})`}
        stroke="rgba(27,24,20,0.2)"
      />
      <path
        d="M52 84c6 8 8 104 1 124"
        fill="none"
        stroke="#ffffff"
        strokeOpacity="0.38"
        strokeWidth="5"
        strokeLinecap="round"
      />
      <rect x="44" y="118" width="52" height="44" rx="2" fill="rgba(251,248,242,0.92)" />
      <text
        x="70"
        y="136"
        textAnchor="middle"
        fontSize="8"
        letterSpacing="1.4"
        fill="#8f2424"
        fontFamily="Outfit, sans-serif"
      >
        DR. SALIM
      </text>
      <text x="70" y="154" textAnchor="middle" fontSize="13" fill="#1b1814" fontFamily="Fraunces, Georgia, serif">
        {formula}
      </text>
    </svg>
  );
}
