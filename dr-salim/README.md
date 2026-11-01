# Dr. Salim

Marketing and shop site for Dr. Salim, a perfume house in Mutrah. Browse the cabinet, open a formula, and keep a cart in the browser.

This is a new project. It does not use the XtraPOS API, accounts, payments, or admin.

## Run

```bash
cd dr-salim
npm install
npm run dev
```

Open http://localhost:5174

## Scripts

- `npm run dev` — development server on port 5174
- `npm test` — cart and shop-flow tests
- `npm run build` — typecheck and production build
- `npm run preview` — serve the production build on port 4174

## Pages

- `/` — house introduction
- `/collection` — catalog, filtered by concentration
- `/collection/:slug` — formula, notes, and add to cart
- `/cart` — review quantities and the subtotal
- `/about` — the house
- `/contact` — a note to the atelier, opened in your mail app

The cart is stored in `localStorage` under `dr-salim.cart.v1`. Sending a note does not call a server; it prepares a `mailto:` link to atelier@drsalim.com.
