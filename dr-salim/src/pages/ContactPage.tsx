import { useState, type FormEvent, type ReactNode } from "react";
import { useCart } from "../cart/CartContext.tsx";
import type { CartLine } from "../cart/cart.ts";
import { house } from "../data/house.ts";
import { perfumeBySlug } from "../data/perfumes.ts";
import { formatMoney } from "../format.ts";
import { useRouter } from "../router.tsx";
import { usePageTitle } from "../usePageTitle.ts";

type Fields = {
  name: string;
  email: string;
  message: string;
};

type Errors = Partial<Fields>;

export function ContactPage() {
  usePageTitle("Contact · Dr. Salim");
  const { path } = useRouter();
  const { lines } = useCart();
  const prefill = new URLSearchParams(path.split("?")[1] ?? "").get("selection") === "1";
  const [fields, setFields] = useState<Fields>(() => ({
    name: "",
    email: "",
    message: prefill ? selectionMessage(lines) : "",
  }));
  const [errors, setErrors] = useState<Errors>({});
  const [sent, setSent] = useState<Fields | null>(null);

  function update(key: keyof Fields, value: string) {
    setFields((current) => ({ ...current, [key]: value }));
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const next = validate(fields);
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    setSent(fields);
  }

  return (
    <>
      <header className="page-intro">
        <div>
          <p className="eyebrow">Atelier</p>
          <h1>Write to the house</h1>
        </div>
        <p className="lede">
          Ask for a formula, a visit, or a material that has gone quiet. {house.hours}. {house.visits}.
        </p>
      </header>
      <div className="letter">
        <aside>
          <p className="formula-no">Address</p>
          <p>
            {house.street}
            <br />
            {house.city}
          </p>
          <p>
            <a href={`mailto:${house.email}`}>{house.email}</a>
          </p>
          <p className="fine-print">{house.hours}</p>
        </aside>
        {sent ? (
          <div className="sent" role="status">
            <h2>The note is ready.</h2>
            <p>
              Nothing is stored on a server. Open your mail app to send it to the atelier from {sent.email}.
            </p>
            <a className="button solid" href={mailHref(sent)}>
              Email the atelier
            </a>
          </div>
        ) : (
          <form onSubmit={submit} noValidate>
            <Field label="Name" error={errors.name}>
              <input
                value={fields.name}
                autoComplete="name"
                aria-invalid={errors.name ? true : undefined}
                onChange={(event) => update("name", event.target.value)}
              />
            </Field>
            <Field label="Email" error={errors.email}>
              <input
                type="email"
                value={fields.email}
                autoComplete="email"
                aria-invalid={errors.email ? true : undefined}
                onChange={(event) => update("email", event.target.value)}
              />
            </Field>
            <Field label="Message" error={errors.message}>
              <textarea
                value={fields.message}
                rows={8}
                aria-invalid={errors.message ? true : undefined}
                onChange={(event) => update("message", event.target.value)}
              />
            </Field>
            <button type="submit" className="button solid">
              Send the note
            </button>
          </form>
        )}
      </div>
    </>
  );
}

function Field({ label, error, children }: { label: string; error?: string; children: ReactNode }) {
  return (
    <div className="field">
      <label>
        <span>{label}</span>
        {children}
      </label>
      {error ? (
        <span className="error" role="alert">
          {error}
        </span>
      ) : null}
    </div>
  );
}

function validate(fields: Fields): Errors {
  const errors: Errors = {};
  if (fields.name.trim().length < 2) errors.name = "Enter your name.";
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(fields.email.trim())) errors.email = "Enter a valid email.";
  if (fields.message.trim().length < 12) errors.message = "Write a note of at least a sentence.";
  return errors;
}

function selectionMessage(lines: readonly CartLine[]): string {
  if (lines.length === 0) return "";
  const rows = lines.map((line) => {
    const perfume = perfumeBySlug(line.slug);
    if (!perfume) return `${line.slug} × ${line.quantity}`;
    return `${perfume.formula} ${perfume.name} × ${line.quantity} — ${formatMoney(perfume.price * line.quantity)}`;
  });
  return `I would like to request:\n\n${rows.join("\n")}\n\nPlease confirm availability.`;
}

function mailHref(fields: Fields): string {
  const body = `${fields.message.trim()}\n\n— ${fields.name.trim()}\n${fields.email.trim()}`;
  return `mailto:${house.email}?subject=${encodeURIComponent("A note for the atelier")}&body=${encodeURIComponent(body)}`;
}
