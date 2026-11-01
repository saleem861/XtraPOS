import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";

type RouterValue = {
  path: string;
  navigate: (to: string) => void;
};

const RouterContext = createContext<RouterValue | null>(null);

function currentLocation(): string {
  return `${window.location.pathname}${window.location.search}`;
}

export function Router({ children }: { children: ReactNode }) {
  const [path, setPath] = useState(currentLocation);

  useEffect(() => {
    const onPop = () => setPath(currentLocation());
    window.addEventListener("popstate", onPop);
    return () => window.removeEventListener("popstate", onPop);
  }, []);

  const navigate = useCallback((to: string) => {
    const next = to.startsWith("/") ? to : `/${to}`;
    if (next !== currentLocation()) {
      window.history.pushState({}, "", next);
    }
    setPath(next);
    window.scrollTo(0, 0);
  }, []);

  return <RouterContext.Provider value={{ path, navigate }}>{children}</RouterContext.Provider>;
}

export function useRouter(): RouterValue {
  const value = useContext(RouterContext);
  if (!value) throw new Error("useRouter must be used inside Router");
  return value;
}

type LinkProps = {
  to: string;
  className?: string;
  children: ReactNode;
  onClick?: () => void;
  ariaLabel?: string;
};

export function Link({ to, className, children, onClick, ariaLabel }: LinkProps) {
  const { navigate, path } = useRouter();
  const currentPath = path.split("?")[0] ?? "/";
  const target = to.split("?")[0] ?? "/";

  return (
    <a
      href={to}
      className={className}
      aria-current={currentPath === target ? "page" : undefined}
      aria-label={ariaLabel}
      onClick={(event) => {
        if (event.defaultPrevented || event.button !== 0) return;
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        onClick?.();
        navigate(to);
      }}
    >
      {children}
    </a>
  );
}
