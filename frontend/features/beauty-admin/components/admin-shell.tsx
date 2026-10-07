"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, Suspense, useId, useState, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AuthGate, useAuth } from "@/features/beauty-auth/components/auth-provider";
import { navFor } from "@/features/beauty-auth/permissions";
import { ROLE_LABEL } from "@/features/beauty-auth/types";
import { useAiPanel } from "../hooks/use-ai-panel";
import { aiContextFor } from "./ai-contexts";

function isActive(pathname: string, href: string) {
  if (href === "/beauty") return pathname === "/beauty";
  return pathname === href || pathname.startsWith(`${href}/`);
}

export function SideNav() {
  const pathname = usePathname();
  const { user, logout } = useAuth();
  const [leaving, setLeaving] = useState(false);
  const nav = navFor(user.role);
  return (
    <nav
      aria-label="Головне меню"
      className="flex flex-none flex-col gap-1 bg-(--ink) px-5 py-7 lg:sticky lg:top-0 lg:h-screen lg:w-[260px] lg:overflow-y-auto"
    >
      <div className="beauty-display mb-6 text-xl font-semibold text-white">Beauty Lab</div>
      {nav.map((n) => {
        const active = isActive(pathname, n.href);
        return (
          <Link
            key={n.href}
            href={n.href}
            aria-current={active ? "page" : undefined}
            className={`block min-h-11 rounded-xl px-3.5 py-3 text-[15px] text-white no-underline ${active ? "bg-(--accent) font-semibold" : "font-normal hover:bg-white/10"}`}
          >
            {n.label}
          </Link>
        );
      })}
      {user.role === "specialist" ? null : (
        <Link
          href="/book"
          className="block min-h-11 rounded-xl px-3.5 py-3 text-[15px] font-normal text-white no-underline hover:bg-white/10"
        >
          Запис клієнта
        </Link>
      )}
      <div className="mt-auto flex flex-col gap-2 border-t border-white/20 pt-4">
        <div className="text-sm text-white">
          <div className="font-semibold break-words">{user.fullName}</div>
          <div className="text-[13px] text-[#D9D4E4]">{ROLE_LABEL[user.role]}</div>
        </div>
        <button
          type="button"
          disabled={leaving}
          onClick={() => {
            setLeaving(true);
            void logout();
          }}
          className="min-h-11 cursor-pointer rounded-xl border border-white/40 bg-transparent px-3.5 text-left text-[15px] font-semibold text-white hover:bg-white/10 disabled:cursor-not-allowed disabled:opacity-60"
        >
          {leaving ? "Виходимо…" : "Вийти"}
        </button>
      </div>
    </nav>
  );
}

function AiFab() {
  const { user } = useAuth();
  if (user.role === "specialist") return null;
  return <AiFabPanel />;
}

function AiFabPanel() {
  const pathname = usePathname();
  const { open, toggle, close } = useAiPanel();
  const ctx = aiContextFor(pathname);
  const [question, setQuestion] = useState("");
  const [sent, setSent] = useState<string | null>(null);
  const panelId = useId();
  const inputId = useId();

  // Закриваємо при зміні екрана.
  useEffect(() => {
    close();
  }, [pathname, close]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open, close]);

  return (
    <div className="fixed right-6 bottom-6 z-50 flex flex-col items-end gap-3">
      {open ? (
        <div
          id={panelId}
          role="dialog"
          aria-label="AI-асистент"
          className="flex max-h-[calc(100vh-120px)] w-[340px] max-w-[calc(100vw-48px)] flex-col overflow-y-auto rounded-[20px] bg-white shadow-[0_12px_40px_rgba(30,27,46,0.28)]"
        >
          <div className="flex flex-col gap-0.5 bg-(--ink) px-4 py-3.5 text-white">
            <span className="beauty-display text-[15px] font-semibold">AI-асистент</span>
            <span className="text-xs text-[#D9D4E4]">Аналізує: {ctx.title}</span>
          </div>
          <div className="flex flex-col gap-2.5 px-4 py-3.5">
            <div className="rounded-xl bg-(--tint) px-3 py-2.5 text-[13px] leading-snug">
              <strong>{ctx.insight.heading}</strong>
              <br />
              {ctx.insight.body}
            </div>
            <div className="max-w-[85%] self-end rounded-[14px] bg-(--accent) px-3 py-2 text-[13px] text-white">{ctx.question}</div>
            <div className="max-w-[90%] self-start rounded-[14px] bg-(--page) px-3 py-2 text-[13px] leading-snug">{ctx.answer}</div>
            {sent ? (
              <div role="status" className="self-end rounded-[14px] bg-(--accent) px-3 py-2 text-[13px] text-white">
                {sent}
              </div>
            ) : null}
            <div className="flex flex-wrap gap-1.5">
              {ctx.links.map((l) => (
                <Link
                  key={l.href}
                  href={l.href}
                  className="inline-flex min-h-11 items-center rounded-[22px] border border-(--accent) px-3.5 text-[13px] font-semibold text-(--accent) no-underline"
                >
                  {l.label}
                </Link>
              ))}
            </div>
          </div>
          <form
            className="flex gap-2 border-t border-(--line) px-3 py-2.5"
            onSubmit={(e) => {
              e.preventDefault();
              if (!question.trim()) return;
              setSent(question.trim());
              setQuestion("");
            }}
          >
            <label htmlFor={inputId} className="sr-only">
              Запитання до AI
            </label>
            <input
              id={inputId}
              type="text"
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              placeholder="Запитайте AI"
              className="h-11 min-w-0 flex-1 rounded-[22px] border border-(--line-strong) px-3.5 text-sm"
            />
            <button type="submit" aria-label="Надіслати" className="flex size-11 cursor-pointer items-center justify-center rounded-full border-0 bg-(--accent) p-0">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#FFFFFF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M5 12h14M13 6l6 6-6 6" />
              </svg>
            </button>
          </form>
        </div>
      ) : null}
      <button
        type="button"
        aria-label={open ? "Закрити AI-асистента" : "Відкрити AI-асистента"}
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={toggle}
        className="flex size-[60px] cursor-pointer items-center justify-center rounded-full border-0 bg-(--accent) p-0 shadow-[0_8px_24px_rgba(30,27,46,0.35)]"
      >
        <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#FFFFFF" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z" />
          <path d="M19 16l.7 2 2 .7-2 .7-.7 2-.7-2-2-.7 2-.7z" />
        </svg>
      </button>
    </div>
  );
}

export function AdminShell({ children }: { children: ReactNode }) {
  const [client] = useState(
    () => new QueryClient({ defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false } } }),
  );
  return (
    <QueryClientProvider client={client}>
      <Suspense fallback={null}>
        <AuthGate>
          <div className="flex min-h-screen flex-col lg:flex-row">
            <SideNav />
            <main className="flex min-w-0 flex-1 flex-col gap-6 px-5 py-7 sm:px-8">{children}</main>
            <AiFab />
          </div>
        </AuthGate>
      </Suspense>
    </QueryClientProvider>
  );
}
