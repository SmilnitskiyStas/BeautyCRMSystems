"use client";

import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { buildTree, filterTree, pathOf, visibleNodes, withoutArchived, type CategoryNode } from "../inventory/categories";
import type { Category } from "../inventory/types";
import { ERROR_TEXT } from "./staff-parts";
import { FIELD_INPUT, FIELD_LABEL } from "./ui";

const NONE = "__none__";

/**
 * Вибір категорії з дерева (ARIA tree): кнопка-поле зі шляхом і випадаюча панель просто під нею (контейнер `relative`,
 * панель `absolute`). Пошук за назвою, «Без категорії», архівні вузли не пропонуються (але вже обрану архівну показуємо
 * з позначкою «архів»). Клавіатура: стрілки, Home/End, → / ← (розгорнути/згорнути/до батька), Enter/Space - вибір,
 * Esc - закрити, Tab - закрити й піти далі.
 */
export function CategoryPicker({
  label,
  categories,
  value,
  onChange,
  noneLabel = "Без категорії",
  disabled,
  hint,
  error,
  fallbackPath,
}: {
  label: string;
  /** Плоский список вузлів (разом з архівними - для підпису обраного). */
  categories: readonly Category[];
  /** Обраний вузол; `null` - «Без категорії». */
  value: string | null;
  onChange: (id: string | null) => void;
  /** Підпис пункту «нічого не обрано» (у фільтрі - «Усі категорії»). */
  noneLabel?: string;
  disabled?: boolean;
  hint?: string;
  error?: string;
  /** Шлях обраної категорії, поки список ще не завантажено. */
  fallbackPath?: string | null;
}) {
  const uid = useId();
  const buttonId = `${uid}-btn`;
  const labelId = `${uid}-lbl`;
  const panelId = `${uid}-panel`;
  const describedBy = [hint ? `${uid}-hint` : "", error ? `${uid}-err` : ""].filter(Boolean).join(" ") || undefined;
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const wantFocus = useRef(false);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set());
  const [activeId, setActiveId] = useState<string>(NONE);

  const tree = useMemo(() => withoutArchived(buildTree(categories)), [categories]);
  const filtered = useMemo(() => filterTree(tree, query), [tree, query]);
  const searching = query.trim() !== "";
  const expandedNow = searching ? filtered.expand : expanded;
  const items = useMemo<{ id: string; node: CategoryNode | null }[]>(
    () => [...(searching ? [] : [{ id: NONE, node: null }]), ...visibleNodes(filtered.nodes, expandedNow).map((n) => ({ id: n.id, node: n }))],
    [filtered.nodes, expandedNow, searching],
  );

  const selected = value ? categories.find((c) => c.id === value) : undefined;
  const display = value ? (selected ? `${pathOf(categories, value)}${selected.isArchived ? " (архів)" : ""}` : (fallbackPath ?? "Категорію не знайдено")) : noneLabel;

  function close(focusButton: boolean) {
    setOpen(false);
    setQuery("");
    if (focusButton) button.current?.focus();
  }

  function toggleOpen() {
    if (open) {
      close(false);
      return;
    }
    // Розгортаємо предків обраної категорії й ставимо фокус на неї.
    const open_ = new Set<string>();
    const by = new Map(categories.map((c) => [c.id, c]));
    let cur = value ? by.get(value) : undefined;
    while (cur?.parentId) {
      open_.add(cur.parentId);
      cur = by.get(cur.parentId);
    }
    setExpanded(open_);
    setActiveId(value && selected && !selected.isArchived ? value : NONE);
    wantFocus.current = true;
    setOpen(true);
  }

  // Фокус на активному пункті після відкриття/навігації; тримаємо його лише коли про це попросили (не при кожному рендері).
  useEffect(() => {
    if (!open || !wantFocus.current) return;
    wantFocus.current = false;
    root.current?.querySelector<HTMLElement>(`[data-cat-id="${CSS.escape(activeId)}"]`)?.focus();
  }, [open, activeId, items]);

  // Клік поза компонентом закриває панель.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) {
        setOpen(false);
        setQuery("");
      }
    };
    document.addEventListener("pointerdown", onDown);
    return () => document.removeEventListener("pointerdown", onDown);
  }, [open]);

  function choose(id: string) {
    onChange(id === NONE ? null : id);
    close(true);
  }
  function moveTo(id: string | undefined) {
    if (!id) return;
    const el = root.current?.querySelector<HTMLElement>(`[data-cat-id="${CSS.escape(id)}"]`);
    if (el) el.focus();
    else wantFocus.current = true;
    setActiveId(id);
  }
  function setNodeExpanded(id: string, on: boolean) {
    if (searching) return;
    setExpanded((cur) => {
      const next = new Set(cur);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  function onTreeKey(e: KeyboardEvent<HTMLUListElement>) {
    const index = items.findIndex((i) => i.id === activeId);
    const current = items[index];
    const node = current?.node ?? null;
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        moveTo(items[Math.min(items.length - 1, index + 1)]?.id);
        break;
      case "ArrowUp":
        e.preventDefault();
        if (index <= 0) search.current?.focus();
        else moveTo(items[index - 1]?.id);
        break;
      case "Home":
        e.preventDefault();
        moveTo(items[0]?.id);
        break;
      case "End":
        e.preventDefault();
        moveTo(items[items.length - 1]?.id);
        break;
      case "ArrowRight":
        e.preventDefault();
        if (node && node.children.length > 0) {
          if (!expandedNow.has(node.id)) setNodeExpanded(node.id, true);
          else moveTo(node.children[0]?.id);
        }
        break;
      case "ArrowLeft":
        e.preventDefault();
        if (node && node.children.length > 0 && expandedNow.has(node.id) && !searching) setNodeExpanded(node.id, false);
        else if (node?.parentId) moveTo(node.parentId);
        break;
      case "Enter":
      case " ":
        e.preventDefault();
        if (current) choose(current.id);
        break;
      default:
    }
  }

  function onRootKey(e: KeyboardEvent<HTMLDivElement>) {
    if (!open) return;
    if (e.key === "Escape") {
      e.preventDefault();
      e.stopPropagation();
      close(true);
    } else if (e.key === "Tab") {
      // Фокус лишається на кнопці-полі, а Tab природно переходить до наступного/попереднього елемента форми.
      close(true);
    }
  }

  return (
    <div ref={root} onKeyDown={onRootKey} className="relative flex min-w-0 flex-col gap-1.5">
      <label id={labelId} htmlFor={buttonId} className={FIELD_LABEL}>
        {label}
      </label>
      <div data-testid="category-field" className="relative">
      <button
        ref={button}
        id={buttonId}
        type="button"
        disabled={disabled}
        aria-haspopup="tree"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-labelledby={`${labelId} ${buttonId}`}
        aria-describedby={describedBy}
        onClick={toggleOpen}
        className={`${FIELD_INPUT} flex cursor-pointer items-center justify-between gap-2 text-left disabled:cursor-default disabled:bg-(--page) disabled:text-(--muted) ${error ? "border-[#8A1F1F]" : ""}`}
      >
        <span className={`min-w-0 flex-1 truncate ${value ? "" : "text-(--muted)"}`}>{display}</span>
        <span aria-hidden="true" className="flex-none text-xs">
          {open ? "▲" : "▼"}
        </span>
      </button>
      {open ? (
        <div
          id={panelId}
          data-testid="category-panel"
          className="absolute top-full right-0 left-0 z-30 mt-1 flex max-h-80 flex-col gap-2 overflow-y-auto rounded-xl border border-(--line-strong) bg-white p-2 shadow-[0_8px_24px_rgba(30,27,46,0.18)]"
        >
          <input
            ref={search}
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                moveTo(items.find((i) => i.id === activeId)?.id ?? items[0]?.id);
              }
            }}
            placeholder="Пошук категорії"
            aria-label="Пошук категорії"
            autoComplete="off"
            className={FIELD_INPUT}
          />
          <ul role="tree" aria-label={label} onKeyDown={onTreeKey} className="m-0 flex list-none flex-col p-0">
            {items.map(({ id, node }) => {
              const hasKids = !!node && node.children.length > 0;
              const isSelected = id === NONE ? value === null : id === value;
              const level = node ? node.depth : 1;
              return (
                <li
                  key={id}
                  role="treeitem"
                  data-cat-id={id}
                  aria-level={level}
                  aria-selected={isSelected}
                  aria-expanded={hasKids ? expandedNow.has(id) : undefined}
                  tabIndex={id === activeId ? 0 : -1}
                  onClick={() => choose(id)}
                  onFocus={() => setActiveId(id)}
                  style={{ paddingLeft: `${(level - 1) * 20 + 4}px` }}
                  className={`flex min-h-11 cursor-pointer items-center gap-1 rounded-lg pr-2 text-sm text-(--ink) hover:bg-(--tint) focus-visible:ring-2 focus-visible:ring-(--accent) focus-visible:outline-none focus-visible:ring-inset ${
                    isSelected ? "bg-(--tint) font-semibold" : ""
                  }`}
                >
                  {hasKids ? (
                    <span
                      aria-hidden="true"
                      data-testid={`category-toggle-${id}`}
                      onClick={(e) => {
                        e.stopPropagation();
                        setNodeExpanded(id, !expandedNow.has(id));
                      }}
                      className="inline-flex size-8 flex-none items-center justify-center text-xs"
                    >
                      {expandedNow.has(id) ? "▾" : "▸"}
                    </span>
                  ) : (
                    <span aria-hidden="true" className="inline-block size-8 flex-none" />
                  )}
                  <span className="min-w-0 flex-1 break-words">{node ? node.name : noneLabel}</span>
                  {isSelected ? <span aria-hidden="true">✓</span> : null}
                </li>
              );
            })}
            {items.length === 0 ? (
              <li role="none" className="px-2 py-3 text-sm text-(--muted)">
                Нічого не знайдено.
              </li>
            ) : null}
          </ul>
        </div>
      ) : null}
      </div>
      {hint ? (
        <p id={`${uid}-hint`} className="text-[13px] text-(--muted)">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${uid}-err`} className={ERROR_TEXT}>
          {error}
        </p>
      ) : null}
    </div>
  );
}
