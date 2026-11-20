"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canManageInventory, canViewInventory } from "@/features/beauty-auth/permissions";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { useInventoryScope } from "../hooks/use-inventory";
import { humanizeInventoryError, isIntegerUnit } from "../inventory/logic";
import type { InventoryScope, StockUnit } from "../inventory/types";
import { Card, Chip, FIELD_INPUT, FIELD_LABEL, PageHeader, QueryState, SecondaryButton } from "./ui";
import { ERROR_TEXT } from "./staff-parts";

/** Підрозділи складу (§32.9). «Постачальники» - лише для `inventory.manage >= own` (контакти, §32.2). */
const SECTIONS = [
  { href: "/beauty/inventory", label: "Залишки", exact: true },
  { href: "/beauty/inventory/products", label: "Товари" },
  { href: "/beauty/inventory/categories", label: "Категорії" },
  { href: "/beauty/inventory/suppliers", label: "Постачальники", manageOnly: true },
  { href: "/beauty/inventory/batches", label: "Партії й терміни" },
  { href: "/beauty/inventory/movements", label: "Журнал рухів" },
  { href: "/beauty/inventory/stocktakes", label: "Інвентаризації" },
  { href: "/beauty/inventory/alerts", label: "Сповіщення" },
] as const;

function SectionNav() {
  const { user } = useAuth();
  const path = usePathname() ?? "";
  const manage = canManageInventory(user);
  return (
    <nav aria-label="Розділи складу" className="flex flex-wrap gap-2">
      {SECTIONS.filter((s) => !("manageOnly" in s) || manage).map((s) => {
        const active = "exact" in s ? path === s.href : path === s.href || path.startsWith(`${s.href}/`);
        return (
          <Link
            key={s.href}
            href={s.href}
            aria-current={active ? "page" : undefined}
            className={`inline-flex min-h-11 items-center rounded-[22px] border-2 px-4 text-sm font-semibold no-underline ${
              active ? "border-(--accent) bg-(--accent) text-white" : "border-(--line-strong) bg-white text-(--ink)"
            }`}
          >
            {s.label}
          </Link>
        );
      })}
    </nav>
  );
}

/**
 * Каркас екрана складу: заголовок, підрозділи, завантаження `scope`. Без права/модуля - пояснення без запитів;
 * `module_disabled` від сервера - окреме повідомлення. Діти отримують `scope` (заклади scope, права на ціни й каталог).
 */
export function InventoryPage({
  title,
  subtitle,
  actions,
  children,
  requireManage = false,
}: {
  title: string;
  subtitle?: ReactNode;
  /** Кнопки шапки; залежать від `scope` (права), тому з'являються після його завантаження. */
  actions?: (scope: InventoryScope) => ReactNode;
  children: (scope: InventoryScope) => ReactNode;
  requireManage?: boolean;
}) {
  const { user } = useAuth();
  const allowed = canViewInventory(user) && (!requireManage || canManageInventory(user));
  const scope = useInventoryScope(allowed);

  if (!allowed) {
    return (
      <>
        <PageHeader title={title} />
        <Card>
          <p className="text-sm">Цей розділ недоступний для вашого рівня доступу або модуль «Склад» не підключено для вашого бізнесу.</p>
        </Card>
      </>
    );
  }
  const disabled = scope.error instanceof BeautyApiError && scope.error.code === "module_disabled";
  return (
    <>
      <PageHeader title={title} subtitle={subtitle} actions={scope.data && actions ? actions(scope.data) : undefined} />
      <SectionNav />
      {disabled ? (
        <Card>
          <p role="alert" className="text-sm">
            {humanizeInventoryError(scope.error)}
          </p>
        </Card>
      ) : (
        <QueryState isPending={scope.isPending} isError={scope.isError} onRetry={() => scope.refetch()}>
          {scope.data ? children(scope.data) : null}
        </QueryState>
      )}
    </>
  );
}

/** Чіпи «Усі заклади» + заклади scope (як в Огляді). `value === null` - усі. */
export function LocationChips({
  locations,
  value,
  onChange,
  allLabel = "Усі заклади",
  allowAll = true,
}: {
  locations: { id: string; name: string }[];
  value: string | null;
  onChange: (id: string | null) => void;
  allLabel?: string;
  allowAll?: boolean;
}) {
  return (
    <div className="flex flex-wrap gap-2" role="group" aria-label="Заклад">
      {allowAll ? (
        <Chip pressed={value === null} onClick={() => onChange(null)}>
          {allLabel}
        </Chip>
      ) : null}
      {locations.map((l) => (
        <Chip key={l.id} pressed={value === l.id} onClick={() => onChange(l.id)}>
          {l.name}
        </Chip>
      ))}
    </div>
  );
}

/** Підписаний `<select>` з підказкою й помилкою (aria-invalid / aria-describedby). */
export function SelectField({
  label,
  value,
  onChange,
  options,
  error,
  hint,
  required,
  disabled,
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string; disabled?: boolean }[];
  error?: string;
  hint?: string;
  required?: boolean;
  disabled?: boolean;
  placeholder?: string;
}) {
  const id = useId();
  const describedBy = [hint ? `${id}-hint` : "", error ? `${id}-err` : ""].filter(Boolean).join(" ") || undefined;
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <label htmlFor={id} className={FIELD_LABEL}>
        {label}
        {required ? <span aria-hidden="true"> *</span> : null}
      </label>
      <select
        id={id}
        value={value}
        disabled={disabled}
        required={required}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={`${FIELD_INPUT} disabled:bg-(--page) disabled:text-(--muted) aria-[invalid=true]:border-[#8A1F1F]`}
      >
        {placeholder !== undefined ? <option value="">{placeholder}</option> : null}
        {options.map((o) => (
          <option key={o.value} value={o.value} disabled={o.disabled}>
            {o.label}
          </option>
        ))}
      </select>
      {hint ? (
        <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-err`} className={ERROR_TEXT}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** Поле числа/дати з необов'язковим суфіксом одиниці; помилка прив'язана через aria-describedby. */
export function InputField({
  label,
  value,
  onChange,
  error,
  hint,
  suffix,
  type = "text",
  inputMode,
  autoComplete,
  required,
  disabled,
  maxLength,
  list,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  error?: string;
  hint?: string;
  suffix?: string;
  /** `id` елемента `<datalist>` для підказок. */
  list?: string;
  type?: "text" | "date" | "tel" | "email";
  inputMode?: "decimal" | "text" | "numeric" | "tel" | "email";
  /** За замовчуванням `off`; для `type="tel"` - `tel`. */
  autoComplete?: string;
  required?: boolean;
  disabled?: boolean;
  maxLength?: number;
}) {
  const id = useId();
  const describedBy = [hint ? `${id}-hint` : "", error ? `${id}-err` : ""].filter(Boolean).join(" ") || undefined;
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <label htmlFor={id} className={FIELD_LABEL}>
        {label}
        {required ? <span aria-hidden="true"> *</span> : null}
      </label>
      <div className="flex items-center gap-2">
        <input
          id={id}
          type={type}
          value={value}
          inputMode={inputMode}
          required={required}
          disabled={disabled}
          maxLength={maxLength}
          list={list}
          autoComplete={autoComplete ?? (type === "tel" ? "tel" : type === "email" ? "email" : "off")}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          className={`${FIELD_INPUT} min-w-0 disabled:bg-(--page) disabled:text-(--muted) aria-[invalid=true]:border-[#8A1F1F]`}
        />
        {suffix ? <span className="flex-none text-sm text-(--muted)">{suffix}</span> : null}
      </div>
      {hint ? (
        <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-err`} className={ERROR_TEXT}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** Клавіатура для кількості: цілі одиниці (шт/уп) - цифрова, дробові - з роздільником. */
export const quantityInputMode = (unit: StockUnit | undefined): "numeric" | "decimal" => (unit && isIntegerUnit(unit) ? "numeric" : "decimal");

/** Пояснення позначки «*» для форм із обов'язковими полями. */
export function RequiredNote() {
  return <p className="text-[13px] text-(--muted)">Поля, позначені «*», обов’язкові.</p>;
}

/**
 * Після невдалого сабміту фокус переходить на перше поле з `aria-invalid="true"` усередині `ref`.
 * Виклик `request()` після `setErrors`: фокус ставиться в effect, коли помилки вже відрендерені.
 */
export function useFocusInvalid<T extends HTMLElement>(): [attach: (el: T | null) => void, request: () => void] {
  const [el, setEl] = useState<T | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (tick > 0) el?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
  }, [tick, el]);
  return [setEl, () => setTick((t) => t + 1)];
}

/** Підсумок помилок валідації (озвучується одним `role="alert"`). */
export function ErrorSummary({ count }: { count: number }) {
  if (count <= 0) return null;
  return (
    <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
      Виправте помилки у формі: {count}.
    </p>
  );
}

/**
 * Кнопка «Показати ще» для курсорних списків; статус (`role="status"`) повідомляє про довантаження. Коли кнопка зникає
 * (`hasMore=false`), а фокус був на ній, він повертається на статус - інакше втрачається на `body`.
 */
export function LoadMore({ hasMore, loading, onClick, shown }: { hasMore: boolean; loading: boolean; onClick: () => void; shown: number }) {
  const status = useRef<HTMLParagraphElement>(null);
  const clicked = useRef(false);
  useEffect(() => {
    if (!hasMore && clicked.current) {
      clicked.current = false;
      status.current?.focus();
    }
  }, [hasMore]);
  return (
    <div className="flex flex-wrap items-center gap-3">
      <p ref={status} tabIndex={-1} role="status" className="text-[13px] text-(--muted) outline-none focus-visible:outline-2 focus-visible:outline-(--accent)">
        Показано: {shown}
      </p>
      {hasMore ? (
        <SecondaryButton
          onClick={() => {
            if (loading) return;
            clicked.current = true;
            onClick();
          }}
          aria-disabled={loading || undefined}
        >
          {loading ? "Завантажуємо…" : "Показати ще"}
        </SecondaryButton>
      ) : null}
    </div>
  );
}

export const locationName = (locations: { id: string; name: string }[], id: string) => locations.find((l) => l.id === id)?.name ?? "—";
