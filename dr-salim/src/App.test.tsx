import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { App } from "./App.tsx";

beforeEach(() => {
  localStorage.clear();
  window.history.pushState({}, "", "/");
});

afterEach(() => {
  cleanup();
  localStorage.clear();
});

describe("Dr. Salim shop", () => {
  it("walks from the cabinet to a cart and back to empty", () => {
    render(<App />);

    expect(screen.getByRole("heading", { level: 1, name: /Written like\s*a prescription\./ })).toBeTruthy();
    fireEvent.click(screen.getByRole("link", { name: "Enter the collection" }));

    expect(screen.getByRole("heading", { level: 1, name: "The collection" })).toBeTruthy();
    expect(screen.getByRole("link", { name: /Night Clinic/ })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Eau de Cologne" }));
    expect(screen.queryByRole("link", { name: /Night Clinic/ })).toBeNull();
    expect(screen.getByRole("link", { name: /Citrus Ward/ })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "All" }));
    fireEvent.click(screen.getByRole("link", { name: /Night Clinic/ }));
    expect(screen.getByRole("heading", { level: 1, name: "Night Clinic" })).toBeTruthy();
    expect(screen.getByText(/Bitter orange/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Add to cart" }));
    expect(screen.getByRole("status").textContent).toContain("Added to your cart.");
    fireEvent.click(screen.getByRole("link", { name: "Cart, 1 item" }));

    expect(screen.getByRole("heading", { level: 1, name: "Your cart" })).toBeTruthy();
    expect(screen.getByText("Subtotal").closest("p")?.textContent).toContain("185");

    fireEvent.click(screen.getByRole("button", { name: "Increase quantity of Night Clinic" }));
    expect(screen.getByRole("link", { name: "Cart, 2 items" })).toBeTruthy();
    expect(screen.getByText("Subtotal").closest("p")?.textContent).toContain("370");

    fireEvent.click(screen.getByRole("button", { name: "Remove Night Clinic" }));
    expect(screen.getByText("Your cart is empty.")).toBeTruthy();
  });

  it("keeps the cart on this device", () => {
    const first = render(<App />);
    fireEvent.click(screen.getByRole("link", { name: "Enter the collection" }));
    fireEvent.click(screen.getByRole("link", { name: /Saffron Study/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add to cart" }));
    first.unmount();

    window.history.pushState({}, "", "/cart");
    render(<App />);
    expect(screen.getByRole("heading", { level: 2, name: "Saffron Study" })).toBeTruthy();
  });

  it("tells the house story and prepares a note", () => {
    render(<App />);
    fireEvent.click(screen.getAllByRole("link", { name: "The house" })[0]!);
    expect(screen.getByRole("heading", { level: 1, name: "The house" })).toBeTruthy();
    expect(screen.getByText(/ledger was renumbered/)).toBeTruthy();

    fireEvent.click(screen.getAllByRole("link", { name: "Contact" })[0]!);
    fireEvent.click(screen.getByRole("button", { name: "Send the note" }));
    expect(screen.getByText("Enter your name.")).toBeTruthy();
    expect(screen.getByText("Enter a valid email.")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Lina Haddad" } });
    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "lina@example.com" } });
    fireEvent.change(screen.getByLabelText("Message"), {
      target: { value: "Please hold a bottle of Night Clinic for Saturday." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send the note" }));

    expect(screen.getByRole("heading", { name: "The note is ready." })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Email the atelier" }).getAttribute("href")).toContain(
      "mailto:atelier@drsalim.com",
    );
  });

  it("shows a missing formula and a missing page", () => {
    window.history.pushState({}, "", "/collection/not-a-formula");
    const missingFormula = render(<App />);
    expect(screen.getByRole("heading", { name: "This formula is not in the cabinet." })).toBeTruthy();
    missingFormula.unmount();

    window.history.pushState({}, "", "/missing-formula");
    render(<App />);
    expect(screen.getByRole("heading", { name: "This page is not in the ledger." })).toBeTruthy();
  });
});
