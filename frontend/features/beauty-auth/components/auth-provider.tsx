"use client";

import { useQueryClient } from "@tanstack/react-query";
import { usePathname, useRouter } from "next/navigation";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { BeautyApiError } from "../errors";
import Link from "next/link";
import { ROLE_PRESETS, accessOf, type Access } from "../access";
import { canOpen, homeFor } from "../permissions";
import { clearSession, logout as apiLogout, onSessionEnd, onUserChange, refreshSession, reloadMe, reloadMeSoon } from "../session";
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

/**
 * Права поточного користувача (§30.8): `can(key, level)`, `level(key)`, `hasModule(m)`. Береться з `me.permissions`;
 * до першого `me` (mock/старий backend) - пресет ролі лише для відображення, backend перевіряє незалежно.
 */
export function useAccess(): Access {
  const { user } = useAuth();
  return useMemo(() => accessOf(user), [user]);
}

/** Демо-користувач для `NEXT_PUBLIC_USE_MOCK=1` (роль: `NEXT_PUBLIC_MOCK_ROLE`, за замовчуванням owner). */
function mockUser(): SessionUser {
  const role = (["owner", "admin", "deputy", "specialist"] as Role[]).find((r) => r === process.env.NEXT_PUBLIC_MOCK_ROLE) ?? "owner";
  return {
    permissions: { ...ROLE_PRESETS[role] },
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

/** Відкрита сторінка без доступу (§30.8): права могли змінитися, поки вкладка була відкрита. */
export function AccessChanged({ home }: { home: string }) {
  return (
    <div role="alert" className="flex min-h-screen flex-col items-center justify-center gap-3 px-6 text-center text-sm">
      <h1 className="text-lg font-semibold">Доступ змінено керівником</h1>
      <p className="max-w-sm text-(--muted)">У вас більше немає доступу до цієї сторінки. Якщо це помилка, зверніться до керівника.</p>
      <Link href={home} className="inline-flex min-h-[44px] items-center rounded-lg border px-4 font-semibold">
        На головну
      </Link>
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
  /** Тимчасовий збій перевірки сесії (мережа, 429, 5xx): сесію не завершуємо, пропонуємо повторити. */
  const [blocked, setBlocked] = useState(false);
  const [attempt, setAttempt] = useState(0);

  const toLogin = useCallback(() => {
    qc.clear();
    setUser(null);
    const next = typeof window === "undefined" ? "/beauty" : window.location.pathname + window.location.search;
    router.replace(`/login?next=${encodeURIComponent(next)}`);
  }, [qc, router]);

  useEffect(() => {
    if (USE_MOCK) return;
    let alive = true;
    // Права беремо з `me` (§30.9.1): refresh віддає лише профіль. Помилка `me` трактується як збій сесії (повторити).
    refreshSession()
      .then(() => reloadMe())
      .then((u) => {
        if (alive) setUser(u);
      })
      .catch((e: unknown) => {
        if (!alive) return;
        // Лише явне відхилення сесії (401) веде на логін; 429/5xx/мережа — повторна спроба.
        if (e instanceof BeautyApiError && e.status === 401) toLogin();
        else setBlocked(true);
      });
    const off = onSessionEnd(() => {
      if (alive) toLogin();
    });
    // Нові права після перечитування `me` (X-Access-Version, 403, focus) оновлюють меню й маршрути без перезавантаження.
    const offUser = onUserChange((u) => {
      if (!alive) return;
      setUser((prev) => {
        // Нові права = дані в кеші могли змінити видимість полів (ціни, контакти): оновлюємо активні запити.
        if (prev && prev.accessStamp !== u.accessStamp) void qc.invalidateQueries();
        return u;
      });
    });
    return () => {
      alive = false;
      off();
      offUser();
    };
  }, [toLogin, attempt, qc]);

  // §30.8: вкладка повертає фокус - перечитуємо `me`.
  useEffect(() => {
    if (USE_MOCK) return;
    const onVisible = () => {
      if (document.visibilityState === "visible") reloadMeSoon();
    };
    document.addEventListener("visibilitychange", onVisible);
    window.addEventListener("focus", onVisible);
    return () => {
      document.removeEventListener("visibilitychange", onVisible);
      window.removeEventListener("focus", onVisible);
    };
  }, []);

  const allowed = !user || canOpen(user, pathname);
  // Кореневий `/beauty` без права «Огляд» непомітно веде на домашню сторінку; інші закриті сторінки пояснюємо (§30.8).
  const redirectHome = !!user && !allowed && (pathname === "/beauty" || pathname === "/beauty/");
  useEffect(() => {
    if (user && redirectHome) router.replace(homeFor(user));
  }, [user, redirectHome, router]);

  const logout = useCallback(async () => {
    if (!USE_MOCK) await apiLogout();
    else clearSession(false);
    qc.clear();
    router.replace("/login");
  }, [qc, router]);

  const value = useMemo(() => (user ? { user, logout } : null), [user, logout]);

  if (!value && blocked) {
    return (
      <div role="alert" className="flex min-h-screen flex-col items-center justify-center gap-3 text-sm text-(--muted)">
        <p>Не вдалося перевірити сесію. Спробуйте за хвилину.</p>
        <button
          type="button"
          className="min-h-[44px] rounded-lg border px-4 font-semibold"
          onClick={() => {
            setBlocked(false);
            setAttempt((n) => n + 1);
          }}
        >
          Повторити
        </button>
      </div>
    );
  }
  if (!value) return <Splash text="Перевіряємо сесію…" />;
  if (redirectHome) return <Splash text="Перенаправляємо…" />;
  if (!allowed) return <AccessChanged home={homeFor(value.user)} />;
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
