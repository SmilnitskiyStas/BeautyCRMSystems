"use client";

import { humanizeMarketingError } from "../marketing/logic";
import { Card, SecondaryButton } from "./ui";

/** Функцію вимкнено (`marketing_disabled` / `module_disabled`, §26.11.1): пояснення замість екрана. */
export function MarketingDisabledNotice({ compact = false }: { compact?: boolean }) {
  return (
    <Card as="div" className="flex flex-col gap-2">
      <h2 className="text-lg font-semibold">Розсилки вимкнено</h2>
      <p data-testid="marketing-disabled" className="m-0 text-sm text-(--muted)">
        {compact
          ? "Розсилки й купони не підключені для вашого закладу."
          : "Розсилки й купони не підключені для вашого закладу. Щоб їх увімкнути, зверніться до підтримки. Згоду клієнтів на розсилки можна переглядати в картці клієнта."}
      </p>
    </Card>
  );
}

/** Розділ недоступний ролі (specialist): без запитів і без даних. */
export function MarketingForbiddenNotice() {
  return (
    <Card as="div">
      <p role="alert" className="m-0 text-sm">
        Розсилки доступні власнику й адміністратору.
      </p>
    </Card>
  );
}

export function ErrorLine({ error, fallback, onRetry }: { error: unknown; fallback?: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
      <span>{typeof error === "string" ? error : humanizeMarketingError(error, fallback)}</span>
      {onRetry ? <SecondaryButton onClick={onRetry}>Спробувати ще раз</SecondaryButton> : null}
    </div>
  );
}
