"use client";

import { useId } from "react";
import { priceText, statusReasonText } from "../ai-settings/messages";
import type { AiCatalog, AiRoleId, AiSettings } from "../types";
import { AiKeyField } from "./ai-key-field";
import { Badge } from "./ui";

export interface RoleSelection {
  provider: string;
  model: string;
}

const DESCRIPTION: Record<AiRoleId, string> = {
  chat: "Проста й дешевша модель для частих відповідей клієнтам.",
  analytics: "Розумніша й дорожча модель для аналізу та порад.",
};
const NOT_IN_USE = " Використовуватиметься функцією «Аналітик-радник» (скоро); зараз лише зберігається.";

const COST_TONE: Record<number, "ok" | "wait" | "promo"> = { 1: "ok", 2: "wait", 3: "promo" };

/** Модель за замовчуванням для ролі в провайдері (з каталогу), інакше перша. */
export function pickModel(catalog: AiCatalog, provider: string, role: AiRoleId): string {
  const list = catalog.models.filter((m) => m.provider === provider && m.available);
  return (list.find((m) => m.isDefaultFor.includes(role)) ?? list[0])?.id ?? "";
}

/**
 * Блок одного сценарію (§20.9 п. 3): стан, провайдер, модель з рівнями й цінами з API, ключ поруч (лише owner).
 */
export function AiRoleBlock({
  role,
  catalog,
  settings,
  selection,
  other,
  canManageKey,
  roleLabels,
  onChange,
}: {
  role: AiRoleId;
  catalog: AiCatalog;
  settings: AiSettings;
  selection: RoleSelection;
  /** Інший сценарій: його id й обраний у чернетці провайдер. */
  other: { role: AiRoleId; provider: string };
  canManageKey: boolean;
  roleLabels: Record<AiRoleId, string>;
  onChange: (next: RoleSelection) => void;
}) {
  const id = useId();
  const headingId = `${id}-h`;
  const roleMeta = catalog.roles.find((r) => r.id === role);
  const status = settings.status[role];
  const provider = catalog.providers.find((p) => p.id === selection.provider);
  const providerLabel = provider?.label ?? selection.provider;
  const models = catalog.models.filter((m) => m.provider === selection.provider && m.available);
  const saved = settings[role];
  const keyInfo = settings.keys?.find((k) => k.provider === selection.provider);

  return (
    <section aria-labelledby={headingId} className="flex min-w-0 flex-col gap-4 rounded-xl border border-(--line-strong) p-4" data-role={role}>
      <div className="flex flex-col gap-1">
        <h3 id={headingId} className="text-base font-semibold">
          {roleLabels[role]}
        </h3>
        <p className="text-sm text-(--muted)">
          {DESCRIPTION[role]}
          {roleMeta?.inUse === false ? NOT_IN_USE : ""}
        </p>
      </div>

      <p className="flex flex-wrap items-center gap-2 text-sm" data-testid={`status-${role}`}>
        <Badge tone={status.state === "ready" ? "ok" : "wait"}>{status.state === "ready" ? "Готово" : "Не працює"}</Badge>
        <span>{status.state === "ready" ? "Сценарій готовий до роботи." : statusReasonText(status.reason, providerLabel)}</span>
      </p>
      {saved.modelFallback ? (
        <p role="note" className="rounded-xl bg-(--tint) px-3.5 py-2.5 text-sm">
          Збережену модель вимкнено, тому діє типова: {saved.model}. Оберіть модель і збережіть налаштування.
        </p>
      ) : null}

      <fieldset className="flex flex-col gap-2 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Провайдер</legend>
        <div className="flex flex-wrap gap-2">
          {catalog.providers.map((p) => {
            const rid = `${id}-p-${p.id}`;
            const blocked = !p.available;
            const checked = !blocked && selection.provider === p.id;
            return (
              <label
                key={p.id}
                htmlFor={rid}
                className={`flex min-h-11 cursor-pointer items-center gap-2 rounded-xl border-2 px-3.5 text-sm ${checked ? "border-(--accent) bg-(--tint)" : "border-(--line-strong) bg-white"} ${blocked ? "cursor-not-allowed opacity-70" : ""}`}
              >
                <input
                  id={rid}
                  type="radio"
                  name={`${id}-provider`}
                  value={p.id}
                  checked={checked}
                  aria-disabled={blocked ? true : undefined}
                  aria-describedby={blocked ? `${rid}-why` : undefined}
                  onChange={() => {
                    if (blocked || selection.provider === p.id) return;
                    onChange({ provider: p.id, model: pickModel(catalog, p.id, role) });
                  }}
                  className="size-5 accent-(--accent)"
                />
                <span className="font-semibold">{p.label}</span>
                {blocked ? (
                  <>
                    <Badge tone="neutral" className="!px-2 !py-0.5 !text-xs">
                      {p.availabilityLabel ?? "Скоро"}
                    </Badge>
                    <span id={`${rid}-why`} className="sr-only">
                      Провайдер ще недоступний, обрати його не можна.
                    </span>
                  </>
                ) : null}
              </label>
            );
          })}
        </div>
      </fieldset>

      <fieldset className="flex flex-col gap-2 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Модель</legend>
        {models.length === 0 ? (
          <p className="text-sm text-(--muted)">Для цього провайдера моделей поки немає.</p>
        ) : (
          <div className="grid gap-2 sm:grid-cols-3">
            {models.map((m) => {
              const mid = `${id}-m-${m.id}`;
              const checked = selection.model === m.id;
              return (
                <label
                  key={m.id}
                  htmlFor={mid}
                  className={`flex min-h-11 cursor-pointer flex-col gap-1 rounded-xl border-2 p-3 text-sm ${checked ? "border-(--accent) bg-(--tint)" : "border-(--line-strong) bg-white"}`}
                >
                  <span className="flex items-center gap-2">
                    <input
                      id={mid}
                      type="radio"
                      name={`${id}-model`}
                      value={m.id}
                      checked={checked}
                      aria-describedby={`${mid}-d`}
                      onChange={() => onChange({ ...selection, model: m.id })}
                      className="size-5 accent-(--accent)"
                    />
                    <span className="font-semibold">{m.tierLabel}</span>
                    <Badge tone={COST_TONE[m.costRank] ?? "neutral"} className="!px-2 !py-0.5 !text-xs">
                      {m.costLabel}
                    </Badge>
                  </span>
                  <span id={`${mid}-d`} className="flex flex-col gap-0.5 text-[13px] text-(--muted)">
                    <span className="break-all">{m.id}</span>
                    <span>{priceText(m)}</span>
                  </span>
                </label>
              );
            })}
          </div>
        )}
      </fieldset>

      {canManageKey ? (
        <AiKeyField
          role={role}
          roleLabel={roleLabels[role]}
          roleLabels={roleLabels}
          providerId={selection.provider}
          providerLabel={providerLabel}
          info={keyInfo}
          keySource={settings.keySource}
          otherRole={other.role}
          otherDraftSameProvider={other.provider === selection.provider}
        />
      ) : null}
    </section>
  );
}
