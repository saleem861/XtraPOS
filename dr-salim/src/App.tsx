import { CartProvider } from "./cart/CartContext.tsx";
import { Layout } from "./components/Layout.tsx";
import { AboutPage } from "./pages/AboutPage.tsx";
import { CartPage } from "./pages/CartPage.tsx";
import { CatalogPage } from "./pages/CatalogPage.tsx";
import { ContactPage } from "./pages/ContactPage.tsx";
import { HomePage } from "./pages/HomePage.tsx";
import { NotFoundPage } from "./pages/NotFoundPage.tsx";
import { ProductPage } from "./pages/ProductPage.tsx";
import { Router, useRouter } from "./router.tsx";

export function App() {
  return (
    <Router>
      <CartProvider>
        <Layout>
          <PageSwitch />
        </Layout>
      </CartProvider>
    </Router>
  );
}

function PageSwitch() {
  const { path } = useRouter();
  const pathname = path.split("?")[0] || "/";

  if (pathname === "/") return <HomePage />;
  if (pathname === "/collection") return <CatalogPage />;
  if (pathname.startsWith("/collection/")) {
    const slug = readSlug(pathname.slice("/collection/".length));
    return <ProductPage key={slug} slug={slug} />;
  }
  if (pathname === "/cart") return <CartPage />;
  if (pathname === "/about") return <AboutPage />;
  if (pathname === "/contact") return <ContactPage key={path} />;
  return <NotFoundPage />;
}

function readSlug(raw: string): string {
  try {
    return decodeURIComponent(raw);
  } catch {
    return raw;
  }
}
