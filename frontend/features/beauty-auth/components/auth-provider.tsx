"use client";

import { useQueryClient } from "@tanstack/react-query";
import { usePathname, useRouter } from "next/navigation";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { canOpen, homeFor } from "../permissions";
import { clearSession, logout as apiLogout, onSessionEnd, refreshSession } from "../session";
import { USE_MOCK, type Role, type SessionUser } from "../types";

interface AuthValue {
  user: SessionUser;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

export function useAuth(): AuthValue {
  const v = useContext(AuthContext);
  if (!v) throw new Error("useAuth має викликатися всередині AuthGate");
  return v;
}

/** Демо-користувач для `NEXT_PUBLIC_USE_MOCK=1` (роль: `NEXT_PUBLIC_MOCK_ROLE`, за замовчуванням owner). */
function mockUser(): SessionUser {
  const role = (["owner", "admin", "specialist"] as Role[]).find((r) => r === process.env.NEXT_PUBLIC_MOCK_ROLE) ?? "owner";
  return {
    id: "mock-user",
    email: "demo@beauty.test",
    fullName: "Демо користувач",
    role,
    specialistId: role === "specialist" ? "m" : null,
  };
}

function Splash({ text }: { text: string }) {
  return (
    <div role="status" aria-live="polite" className="flex min-h-screen items-center justify-center text-sm text-(--muted)">
      {text}
    </div>
  );
}

/**
 * Захищає /beauty/*: тихо оновлює сесію (access-токен лише в пам'яті), редіректить на /login,
 * застосовує рольові обмеження маршрутів.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const qc = useQueryClient();
  const [user, setUser] = useState<SessionUser | null>(USE_MOCK ? mockUser() : null);

  const toLogin = useCallback(() => {
    qc.clear();
    setUser(null);
    const next = typeof window === "undefined" ? "/beauty" : window.location.pathname + window.location.search;
    router.replace(`/login?next=${encodeURIComponent(next)}`);
  }, [qc, router]);

  useEffect(() => {
    if (USE_MOCK) return;
    let alive = true;
    refreshSession()
      .then((s) => {
        if (alive) setUser(s.user);
      })
      .catch(() => {
        if (alive) toLogin();
      });
    const off = onSessionEnd(() => {
      if (alive) toLogin();
    });
    return () => {
      alive = false;
      off();
    };
  }, [toLogin]);

  const role = user?.role;
  const allowed = !role || canOpen(role, pathname);
  useEffect(() => {
    if (role && !allowed) router.replace(homeFor(role));
  }, [role, allowed, router]);

  const logout = useCallback(async () => {
    if (!USE_MOCK) await apiLogout();
    else clearSession(false);
    qc.clear();
    router.replace("/login");
  }, [qc, router]);

  const value = useMemo(() => (user ? { user, logout } : null), [user, logout]);

  if (!value) return <Splash text="Перевіряємо сесію…" />;
  if (!allowed) return <Splash text="Перенаправляємо…" />;
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
