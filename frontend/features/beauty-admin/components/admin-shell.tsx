"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, Suspense, useId, useMemo, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AuthGate, useAuth } from "@/features/beauty-auth/components/auth-provider";
import { NAV_GROUPS, canUseInbox, canUseMarketing, navFor, type NavItem } from "@/features/beauty-auth/permissions";
import { ROLE_LABEL } from "@/features/beauty-auth/types";
import { useAiPanel } from "../hooks/use-ai-panel";
import { useNeedsReplyCount } from "../hooks/use-inbox";
import { useMarketingAvailability } from "../hooks/use-marketing";
import { aiContextFor } from "./ai-contexts";

function isActive(pathname: string, href: string) {
  if (href === "/beauty") return pathname === "/beauty";
  return pathname === href || pathname.startsWith(`${href}/`);
}

/** Бейдж «потрібна відповідь» біля пункту «Розмови» (§22.11): число для зору, повний підпис для скрінрідера. */
function InboxBadge() {
  const { user } = useAuth();
  const count = useNeedsReplyCount(canUseInbox(user.role)).data ?? 0;
  if (count <= 0) return null;
  return (
    <span data-testid="inbox-badge" className="ml-2 inline-flex min-w-6 items-center justify-center rounded-full bg-white px-1.5 text-[13px] font-bold text-(--ink)">
      <span aria-hidden="true">{count > 99 ? "99+" : count}</span>
      <span className="sr-only">, потрібна відповідь: {count}</span>
    </span>
  );
}

/** Підсумковий індикатор на заголовку згорнутого блоку, що містить «Розмови». */
function InboxDot() {
  const { user } = useAuth();
  const count = useNeedsReplyCount(canUseInbox(user.role)).data ?? 0;
  if (count <= 0) return null;
  return (
    <span data-testid="inbox-group-dot" className="ml-2 inline-flex size-2.5 rounded-full bg-white">
      <span className="sr-only">, потрібна відповідь: {count}</span>
    </span>
  );
}

const STORAGE_KEY = "beauty-nav-collapsed";

let memoryRaw: string | null = null; // лише коли localStorage недоступний
const listeners = new Set<() => void>();

function readRaw(): string {
  try {
    return memoryRaw ?? window.localStorage.getItem(STORAGE_KEY) ?? "";
  } catch {
    return memoryRaw ?? "";
  }
}

function parseCollapsed(raw: string): string[] {
  try {
    const v: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
  } catch {
    return [];
  }
}

function writeCollapsed(ids: string[]) {
  const raw = JSON.stringify(ids);
  try {
    window.localStorage.setItem(STORAGE_KEY, raw);
    memoryRaw = null;
  } catch {
    memoryRaw = raw;
    /* сховище недоступне - стан живе лише в пам'яті */
  }
  listeners.forEach((l) => l());
}

function subscribeCollapsed(cb: () => void) {
  listeners.add(cb);
  window.addEventListener("storage", cb);
  return () => {
    listeners.delete(cb);
    window.removeEventListener("storage", cb);
  };
}

function NavLink({ item, pathname }: { item: NavItem; pathname: string }) {
  const active = isActive(pathname, item.href);
  return (
    <Link
      href={item.href}
      aria-current={active ? "page" : undefined}
      className={`block min-h-11 rounded-xl px-3.5 py-3 text-[15px] text-white no-underline ${active ? "bg-(--accent) font-semibold" : "font-normal hover:bg-white/10"}`}
    >
      {item.label}
      {item.href === "/beauty/inbox" ? <InboxBadge /> : null}
    </Link>
  );
}

function NavGroupSection({
  id,
  label,
  items,
  pathname,
  collapsed,
  onToggle,
}: {
  id: string;
  label: string;
  items: NavItem[];
  pathname: string;
  collapsed: boolean;
  onToggle: () => void;
}) {
  const uid = useId();
  const hasActive = items.some((n) => isActive(pathname, n.href));
  const expanded = hasActive || !collapsed;
  const panelId = `${uid}-nav-${id}`;
  return (
    <div className="flex flex-col gap-1">
      <button
        type="button"
        aria-expanded={expanded}
        aria-controls={panelId}
        onClick={() => {
          if (!hasActive) onToggle();
        }}
        className="flex min-h-11 w-full cursor-pointer items-center justify-between rounded-xl border-0 bg-transparent px-3.5 text-left text-[13px] font-semibold tracking-wide text-white uppercase hover:bg-white/10"
      >
        <span className="flex items-center">
          {label}
          {!expanded && items.some((n) => n.href === "/beauty/inbox") ? <InboxDot /> : null}
        </span>
        <span aria-hidden="true" className={`transition-transform ${expanded ? "rotate-90" : ""}`}>
          ›
        </span>
      </button>
      <div id={panelId} hidden={!expanded} className="flex flex-col gap-1">
        {items.map((n) => (
          <NavLink key={n.href} item={n} pathname={pathname} />
        ))}
      </div>
    </div>
  );
}

const WIDE_QUERY = "(min-width: 1024px)"; // lg: Tailwind
const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])';

function subscribeWide(cb: () => void) {
  if (typeof window.matchMedia !== "function") return () => {};
  const mq = window.matchMedia(WIDE_QUERY);
  mq.addEventListener("change", cb);
  return () => mq.removeEventListener("change", cb);
}

/** true - широкий екран (постійний sidebar). Без matchMedia (SSR, jsdom) вважаємо екран широким. */
function useIsWide(): boolean {
  return useSyncExternalStore(
    subscribeWide,
    () => (typeof window.matchMedia === "function" ? window.matchMedia(WIDE_QUERY).matches : true),
    () => true,
  );
}

/** Кнопка «Меню» у шапці вузького екрана; індикатор «потрібна відповідь» видно й коли меню закрите. */
function MenuButton({
  open,
  panelId,
  onClick,
  buttonRef,
}: {
  open: boolean;
  panelId: string;
  onClick: () => void;
  buttonRef: React.RefObject<HTMLButtonElement | null>;
}) {
  const { user } = useAuth();
  const count = useNeedsReplyCount(canUseInbox(user.role)).data ?? 0;
  return (
    <button
      ref={buttonRef}
      type="button"
      aria-haspopup="dialog"
      aria-expanded={open}
      aria-controls={panelId}
      onClick={onClick}
      className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-xl border border-white/40 bg-transparent px-3.5 text-[15px] font-semibold text-white hover:bg-white/10"
    >
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
        <path d="M4 7h16M4 12h16M4 17h16" />
      </svg>
      Меню
      {count > 0 ? (
        <span data-testid="menu-button-dot" className="inline-flex size-2.5 rounded-full bg-white">
          <span className="sr-only">, потрібна відповідь: {count}</span>
        </span>
      ) : null}
    </button>
  );
}

export function SideNav() {
  const pathname = usePathname();
  const { user, logout } = useAuth();
  const [leaving, setLeaving] = useState(false);
  const wide = useIsWide();
  const [open, setOpen] = useState(false);
  const [prevPath, setPrevPath] = useState(pathname);
  if (prevPath !== pathname) {
    setPrevPath(pathname);
    setOpen(false);
  }
  const drawer = !wide && open;
  const panelId = useId();
  const menuBtn = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLElement>(null);
  const closeBtn = useRef<HTMLButtonElement>(null);

  // Меню відкрите: фокус у панель, скрол сторінки заблоковано, Esc закриває; при закритті фокус - на кнопку «Меню».
  useEffect(() => {
    if (!drawer) return;
    const button = menuBtn.current;
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    closeBtn.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.body.style.overflow = prevOverflow;
      document.removeEventListener("keydown", onKey);
      if (button && document.contains(button)) button.focus();
    };
  }, [drawer]);

  const onPanelKeyDown = (e: ReactKeyboardEvent<HTMLElement>) => {
    if (!drawer || e.key !== "Tab" || !panel.current) return;
    const items = [...panel.current.querySelectorAll<HTMLElement>(FOCUSABLE)];
    if (items.length === 0) return;
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && active === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  };
  // Серверний знімок порожній (усе розгорнуто), збережений стан підхоплюється після гідрації.
  const collapsedRaw = useSyncExternalStore(subscribeCollapsed, readRaw, () => "");
  const collapsedIds = useMemo(() => parseCollapsed(collapsedRaw), [collapsedRaw]);
  const toggle = (id: string) =>
    writeCollapsed(collapsedIds.includes(id) ? collapsedIds.filter((x) => x !== id) : [...collapsedIds, id]);
  // §26.11.1: розсилки вимкнено (`marketing_disabled` / `module_disabled`) - пункт меню не показуємо.
  const marketing = useMarketingAvailability(canUseMarketing(user.role));
  const isSpecialist = user.role === "specialist";
  const visible = (n: NavItem) => !(n.href === "/beauty/campaigns" && marketing.disabled);
  const flat = isSpecialist ? navFor(user.role).filter(visible) : [];
  const groups = isSpecialist
    ? []
    : NAV_GROUPS.map((g) => ({ ...g, items: g.items.filter(visible) })).filter((g) => g.items.length > 0);
  const Panel = drawer ? "div" : "nav";
  const panelProps = drawer
    ? ({ role: "dialog", "aria-modal": true } as const)
    : {};
  return (
    <>
    <header className="sticky top-0 z-30 flex items-center justify-between gap-3 bg-(--ink) px-4 py-2 pt-[max(0.5rem,env(safe-area-inset-top))] lg:hidden">
      <span className="beauty-display text-lg font-semibold text-white">Beauty Lab</span>
      <MenuButton open={drawer} panelId={panelId} buttonRef={menuBtn} onClick={() => setOpen((v) => !v)} />
    </header>
    {drawer ? (
      <div aria-hidden="true" data-testid="menu-backdrop" onClick={() => setOpen(false)} className="beauty-backdrop-in fixed inset-0 z-40 bg-black/50" />
    ) : null}
    {wide || drawer ? (
    <Panel
      id={panelId}
      ref={panel as React.RefObject<never>}
      aria-label="Головне меню"
      {...panelProps}
      onKeyDown={onPanelKeyDown}
      onClick={(e) => {
        if (drawer && (e.target as HTMLElement).closest("a[href]")) setOpen(false);
      }}
      className={
        drawer
          ? "beauty-drawer-in fixed inset-y-0 left-0 z-50 flex w-[min(86vw,320px)] flex-col gap-1 overflow-y-auto overscroll-contain bg-(--ink) px-5 py-7 pb-[max(1.75rem,env(safe-area-inset-bottom))] shadow-[0_12px_40px_rgba(30,27,46,0.35)]"
          : "flex flex-none flex-col gap-1 bg-(--ink) px-5 py-7 max-lg:hidden lg:sticky lg:top-0 lg:h-screen lg:w-[260px] lg:overflow-y-auto"
      }
    >
      <div className="mb-6 flex items-center justify-between gap-3">
        <div className="beauty-display text-xl font-semibold text-white">Beauty Lab</div>
        {drawer ? (
          <button
            ref={closeBtn}
            type="button"
            onClick={() => setOpen(false)}
            className="min-h-11 cursor-pointer rounded-xl border border-white/40 bg-transparent px-3.5 text-[15px] font-semibold text-white hover:bg-white/10"
          >
            Закрити
          </button>
        ) : null}
      </div>
      {isSpecialist ? null : (
        <Link
          href="/book"
          className="mb-3 block min-h-11 rounded-xl border border-white/40 px-3.5 py-3 text-center text-[15px] font-semibold text-white no-underline hover:bg-white/10"
        >
          Запис клієнта
        </Link>
      )}
      {flat.map((n) => (
        <NavLink key={n.href} item={n} pathname={pathname} />
      ))}
      {groups.map((g) => (
        <NavGroupSection
          key={g.id}
          id={g.id}
          label={g.label}
          items={g.items}
          pathname={pathname}
          collapsed={collapsedIds.includes(g.id)}
          onToggle={() => toggle(g.id)}
        />
      ))}
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
    </Panel>
    ) : null}
    </>
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
