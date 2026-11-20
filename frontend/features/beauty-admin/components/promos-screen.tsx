"use client";

import { useRef, useState, type KeyboardEvent } from "react";
import { useSearchParams } from "next/navigation";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canManageCatalog, canUseMarketing } from "@/features/beauty-auth/permissions";
import { useMarketingAvailability } from "../hooks/use-marketing";
import { CodesTab, OffersTab, PricesTab } from "./promo-lists";
import { PageHeader } from "./ui";

type Tab = "prices" | "offers" | "codes";

const TAB_LABEL: Record<Tab, string> = { prices: "Прайс-лист", offers: "Акції", codes: "Коди" };
const DEFAULT_TAB: Tab = "offers";

const isTab = (v: string | null | undefined): v is Tab => v === "prices" || v === "offers" || v === "codes";

/** Активна вкладка живе в URL (`?tab=`): посилання й F5 зберігають розділ; History API синхронізується з `useSearchParams`. */
function writeTab(tab: Tab) {
  const url = new URL(window.location.href);
  url.searchParams.set("tab", tab);
  window.history.replaceState(window.history.state, "", `${url.pathname}${url.search}${url.hash}`);
}

export function PromosScreen() {
  const { user } = useAuth();
  const sp = useSearchParams();
  const marketing = useMarketingAvailability(canUseMarketing(user));
  // Розсилки/купони доступні за правом marketing.campaigns і якщо модуль підключено.
  const marketingOn = canUseMarketing(user) && !marketing.disabled;
  const canCreate = canManageCatalog(user);

  const tabs: Tab[] = marketingOn ? ["prices", "offers", "codes"] : ["prices", "offers"];
  const [picked, setPicked] = useState<string | null>(() => sp?.get("tab") ?? null);
  const tab: Tab = isTab(picked) && tabs.includes(picked) ? picked : DEFAULT_TAB;
  const refs = useRef<Partial<Record<Tab, HTMLButtonElement | null>>>({});

  function select(next: Tab, focus = false) {
    setPicked(next);
    writeTab(next);
    if (focus) refs.current[next]?.focus();
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    const i = tabs.indexOf(tab);
    const target =
      e.key === "ArrowRight" ? tabs[(i + 1) % tabs.length] : e.key === "ArrowLeft" ? tabs[(i - 1 + tabs.length) % tabs.length] : e.key === "Home" ? tabs[0] : e.key === "End" ? tabs[tabs.length - 1] : null;
    if (!target) return;
    e.preventDefault();
    select(target, true);
  }

  return (
    <>
      <PageHeader title="Ціни та акції" subtitle="Прайс мережі, акції та промокоди." />
      <div role="tablist" aria-label="Розділи цін та акцій" onKeyDown={onKeyDown} className="flex flex-wrap gap-2">
        {tabs.map((t) => {
          const active = t === tab;
          return (
            <button
              key={t}
              ref={(el) => {
                refs.current[t] = el;
              }}
              type="button"
              role="tab"
              id={`promos-tab-${t}`}
              aria-selected={active}
              aria-controls={`promos-panel-${t}`}
              tabIndex={active ? 0 : -1}
              onClick={() => select(t)}
              className={`inline-flex min-h-11 cursor-pointer items-center rounded-[22px] border-2 px-4 text-sm font-semibold ${
                active ? "border-(--accent) bg-(--accent) text-white" : "border-(--line-strong) bg-white text-(--ink)"
              }`}
            >
              {TAB_LABEL[t]}
            </button>
          );
        })}
      </div>
      <div role="tabpanel" id={`promos-panel-${tab}`} aria-labelledby={`promos-tab-${tab}`} className="flex min-w-0 flex-col gap-4">
        {tab === "prices" ? <PricesTab /> : null}
        {tab === "offers" ? <OffersTab marketingOn={marketingOn} canCreate={canCreate} canCodes={marketingOn} /> : null}
        {tab === "codes" ? <CodesTab canManage={marketingOn} /> : null}
      </div>
    </>
  );
}
