"use client";

import { useId } from "react";
import type { AccessCatalog, AccessCatalogPermission } from "@/features/beauty-auth/access";
import { blockEntries, levelLabel, menuScopeNote, permTitle, type OptionState } from "../team/logic";

/**
 * Редактор прав: блоки-картки (основний рівень + підпункти), кожне право - група радіокнопок (клавіатура: стрілки).
 * Недоступний варіант лишається видимим, з поясненням текстом (не лише кольором): стеля, модуль, «видано директором», власний рядок.
 * Редактор не знає про сервер: стан варіантів, значення й зміни задає батьківський компонент.
 */
export interface AccessEditorProps {
  catalog: AccessCatalog;
  /** Поточне (з чернеткою) значення ключа. */
  value: (key: string) => string;
  state: (key: string, level: string) => OptionState;
  onChange: (key: string, level: string) => void;
  /** Ключі, змінені відносно збереженого (позначка «змінено»). */
  changed?: ReadonlySet<string>;
  /** Ключі з порушеними обмеженнями (підсвічуються й пояснюються). */
  invalid?: ReadonlyMap<string, string>;
  /** Ключі вимкнених модулів: діє найнижчий рівень. */
  moduleDisabled?: readonly string[];
  /** Префікс id/name, щоб кілька редакторів на сторінці не конфліктували. */
  name: string;
}

export function AccessEditor(props: AccessEditorProps) {
  return (
    <div className="grid gap-4" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(min(100%, 340px), 1fr))" }}>
      {blockEntries(props.catalog).map((b) => (
        <BlockCard key={b.block} {...props} title={b.title} main={b.main} subs={b.subs} />
      ))}
    </div>
  );
}

function BlockCard({
  title,
  main,
  subs,
  ...p
}: AccessEditorProps & { title: string; main: AccessCatalogPermission; subs: AccessCatalogPermission[] }) {
  const headId = useId();
  return (
    <section aria-labelledby={headId} className="flex flex-col gap-3 rounded-2xl border border-(--line) bg-white p-4" data-testid={`block-${main.key.split(".")[0]}`}>
      <h3 id={headId} className="text-base font-semibold">
        {title}
      </h3>
      <PermissionGroup perm={main} {...p} />
      {subs.length > 0 ? (
        <div className="flex flex-col gap-3 border-l-2 border-(--line) pl-3">
          {subs.map((s) => (
            <PermissionGroup key={s.key} perm={s} {...p} sub />
          ))}
        </div>
      ) : null}
    </section>
  );
}

function PermissionGroup({ perm, ...p }: AccessEditorProps & { perm: AccessCatalogPermission; sub?: boolean }) {
  const current = p.value(perm.key);
  const problem = p.invalid?.get(perm.key);
  const uid = useId();
  const moduleId = `${uid}-m`;
  const problemId = `${uid}-p`;
  const moduleOff = p.moduleDisabled?.includes(perm.key);
  return (
    <fieldset
      className={`m-0 flex min-w-0 flex-col gap-1.5 rounded-xl border-0 p-0 ${problem ? "bg-[#FBE4E4] p-2" : ""}`}
      aria-describedby={[moduleOff ? moduleId : "", problem ? problemId : ""].filter(Boolean).join(" ") || undefined}
      aria-invalid={problem ? true : undefined}
      data-testid={`perm-${perm.key}`}
    >
      <legend className="mb-1 p-0 text-sm font-semibold">
        {permTitle(perm.key)}
        {p.changed?.has(perm.key) ? <span className="ml-2 rounded-full bg-(--tint) px-2 py-0.5 text-[12px] font-semibold text-(--ink)">змінено</span> : null}
      </legend>
      {perm.levels.map((level) => {
        const st = p.state(perm.key, level);
        const checked = current === level;
        const menuNote = menuScopeNote(perm, level);
        const descId = `${p.name}-${perm.key}-${level}-d`;
        return (
          <label
            key={level}
            className={`flex min-h-11 items-start gap-2.5 rounded-xl border px-3 py-2 text-sm ${
              checked ? "border-(--accent) bg-(--tint)" : "border-(--line)"
            } ${st.disabled && !checked ? "cursor-not-allowed bg-(--page) text-(--muted)" : "cursor-pointer"}`}
          >
            <input
              type="radio"
              name={`${p.name}-${perm.key}`}
              value={level}
              checked={checked}
              disabled={st.disabled}
              aria-describedby={st.reason || menuNote ? descId : undefined}
              onChange={() => p.onChange(perm.key, level)}
              className="mt-1 size-4 flex-none accent-(--accent)"
            />
            <span className="flex min-w-0 flex-col">
              <span className={checked ? "font-semibold" : ""}>{levelLabel(perm.key, level)}</span>
              {st.reason || menuNote ? (
                <span id={descId} className="text-[13px] text-(--muted)">
                  {st.reason ? `Недоступно: ${st.reason}` : null}
                  {st.reason && menuNote ? ". " : null}
                  {menuNote}
                </span>
              ) : null}
            </span>
          </label>
        );
      })}
      {moduleOff ? (
        <p id={moduleId} className="m-0 text-[13px] text-(--muted)">
          Модуль вимкнено для вашого бізнесу: фактично діє найнижчий рівень, збережене значення повернеться після ввімкнення.
        </p>
      ) : null}
      {problem ? (
        <p id={problemId} role="alert" className="m-0 text-[13px] font-semibold text-[#8A1F1F]">
          {problem}
        </p>
      ) : null}
    </fieldset>
  );
}
