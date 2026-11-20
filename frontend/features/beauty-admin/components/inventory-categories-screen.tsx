"use client";

import { useId, useState, type FormEvent } from "react";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { useCategoryMutations, useInventoryCategories } from "../hooks/use-inventory";
import { MAX_CATEGORY_DEPTH, buildTree, moveTargets, type CategoryNode } from "../inventory/categories";
import { humanizeInventoryError } from "../inventory/logic";
import type { Category } from "../inventory/types";
import { InputField, InventoryPage, SelectField } from "./inventory-parts";
import { ModalDialog } from "./modal-dialog";
import { ErrorBanner, SuccessBanner } from "./staff-parts";
import { Badge, Card, EmptyState, PrimaryButton, QueryState, SecondaryButton } from "./ui";

type Dialog =
  | { kind: "add"; parent: CategoryNode | null }
  | { kind: "rename"; node: CategoryNode }
  | { kind: "move"; node: CategoryNode }
  | { kind: "delete"; node: CategoryNode };

const ROOT = "__root__";

/** Додавання кореневої категорії / підкатегорії та перейменування: одне поле «Назва». */
function NameDialog({ title, initial, submitLabel, busy, error, onSubmit, onClose }: { title: string; initial: string; submitLabel: string; busy: boolean; error: string | null; onSubmit: (name: string) => void; onClose: () => void }) {
  const formId = useId();
  const [name, setName] = useState(initial);
  const [local, setLocal] = useState<string | undefined>();
  function submit(e: FormEvent) {
    e.preventDefault();
    const n = name.trim();
    if (n.length < 1 || n.length > 100) {
      setLocal("Назва: від 1 до 100 символів.");
      return;
    }
    setLocal(undefined);
    onSubmit(n);
  }
  return (
    <ModalDialog
      title={title}
      onClose={onClose}
      busy={busy}
      actions={
        <>
          <PrimaryButton type="submit" form={formId} disabled={busy}>
            {busy ? "Зберігаємо…" : submitLabel}
          </PrimaryButton>
          <SecondaryButton onClick={onClose} disabled={busy}>
            Скасувати
          </SecondaryButton>
        </>
      }
    >
      <form id={formId} onSubmit={submit} noValidate className="flex flex-col gap-3">
        <InputField label="Назва" required maxLength={100} value={name} onChange={setName} error={local} />
        {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      </form>
    </ModalDialog>
  );
}

function MoveDialog({ node, flat, busy, error, onSubmit, onClose }: { node: CategoryNode; flat: readonly Category[]; busy: boolean; error: string | null; onSubmit: (parentId: string | null) => void; onClose: () => void }) {
  const formId = useId();
  const [target, setTarget] = useState("");
  const [local, setLocal] = useState<string | undefined>();
  // Лише коректні батьки: без самого вузла й нащадків, з урахуванням глибини піддерева; не поточний батько.
  const targets = moveTargets(flat, node.id).filter((t) => t.id !== node.parentId);
  const options = [...(node.parentId !== null ? [{ value: ROOT, label: "На верхній рівень (без батька)" }] : []), ...targets.map((t) => ({ value: t.id, label: t.path }))];
  function submit(e: FormEvent) {
    e.preventDefault();
    if (!target) {
      setLocal("Оберіть нового батька.");
      return;
    }
    setLocal(undefined);
    onSubmit(target === ROOT ? null : target);
  }
  return (
    <ModalDialog
      title={`Перемістити «${node.name}»`}
      onClose={onClose}
      busy={busy}
      actions={
        <>
          <PrimaryButton type="submit" form={formId} disabled={busy || options.length === 0}>
            {busy ? "Переміщуємо…" : "Перемістити"}
          </PrimaryButton>
          <SecondaryButton onClick={onClose} disabled={busy}>
            Скасувати
          </SecondaryButton>
        </>
      }
    >
      <form id={formId} onSubmit={submit} noValidate className="flex flex-col gap-3">
        {options.length === 0 ? (
          <p>Немає куди перемістити цю категорію: дерево має не більше {MAX_CATEGORY_DEPTH} рівнів.</p>
        ) : (
          <SelectField label="Новий батько" value={target} onChange={setTarget} placeholder="Оберіть…" options={options} error={local} hint={`Підкатегорії й товари переїдуть разом із категорією. Не більше ${MAX_CATEGORY_DEPTH} рівнів.`} />
        )}
        {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      </form>
    </ModalDialog>
  );
}

function DeleteDialog({ node, busy, error, notEmpty, onDelete, onArchive, onClose }: { node: CategoryNode; busy: boolean; error: string | null; notEmpty: boolean; onDelete: () => void; onArchive: () => void; onClose: () => void }) {
  return (
    <ModalDialog
      title={`Видалити «${node.name}»?`}
      onClose={onClose}
      busy={busy}
      actions={
        <>
          <PrimaryButton onClick={onDelete} disabled={busy}>
            {busy ? "Видаляємо…" : "Видалити"}
          </PrimaryButton>
          {notEmpty && !node.isArchived ? (
            <SecondaryButton onClick={onArchive} disabled={busy}>
              Архівувати
            </SecondaryButton>
          ) : null}
          <SecondaryButton onClick={onClose} disabled={busy}>
            Скасувати
          </SecondaryButton>
        </>
      }
    >
      <p>Категорію буде видалено остаточно. Видалити можна лише порожню категорію.</p>
      {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      {notEmpty ? <p>Щоб прибрати категорію з вибору, не видаляючи її, архівуйте її: товари збережуть свій шлях.</p> : null}
    </ModalDialog>
  );
}

function NodeItem({ node, canEdit, onAdd, onRename, onMove, onToggleArchive, onDelete }: { node: CategoryNode; canEdit: boolean; onAdd: (n: CategoryNode) => void; onRename: (n: CategoryNode) => void; onMove: (n: CategoryNode) => void; onToggleArchive: (n: CategoryNode) => void; onDelete: (n: CategoryNode) => void }) {
  const noteId = useId();
  const lastLevel = node.depth >= MAX_CATEGORY_DEPTH;
  const addBlocked = lastLevel || node.isArchived;
  return (
    <li className="flex flex-col gap-1">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-xl border border-(--line) px-3 py-2">
        <span className={`min-w-0 flex-1 basis-40 text-sm font-semibold break-words ${node.isArchived ? "text-(--muted)" : ""}`}>{node.name}</span>
        <span className="flex flex-wrap gap-2">
          {node.isArchived ? <Badge tone="neutral">Архів</Badge> : null}
          <Badge tone="neutral">Товарів: {node.productCount}</Badge>
          <Badge tone="neutral">Підкатегорій: {node.childCount}</Badge>
        </span>
        {canEdit ? (
          <span className="flex flex-wrap gap-2">
            <SecondaryButton onClick={() => onAdd(node)} disabled={addBlocked} aria-describedby={addBlocked ? noteId : undefined} aria-label={`Додати підкатегорію до «${node.name}»`}>
              Додати підкатегорію
            </SecondaryButton>
            <SecondaryButton onClick={() => onRename(node)} aria-label={`Перейменувати «${node.name}»`}>
              Перейменувати
            </SecondaryButton>
            <SecondaryButton onClick={() => onMove(node)} aria-label={`Перемістити «${node.name}»`}>
              Перемістити
            </SecondaryButton>
            <SecondaryButton onClick={() => onToggleArchive(node)} aria-label={`${node.isArchived ? "Розархівувати" : "Архівувати"} «${node.name}»`}>
              {node.isArchived ? "Розархівувати" : "Архівувати"}
            </SecondaryButton>
            <SecondaryButton onClick={() => onDelete(node)} aria-label={`Видалити «${node.name}»`}>
              Видалити
            </SecondaryButton>
          </span>
        ) : null}
        {canEdit && addBlocked ? (
          <span id={noteId} className="basis-full text-[13px] text-(--muted)">
            {lastLevel ? `Це ${MAX_CATEGORY_DEPTH}-й (останній) рівень: підкатегорії тут додати не можна.` : "Категорія в архіві: спершу розархівуйте її."}
          </span>
        ) : null}
      </div>
      {node.children.length > 0 ? (
        <ul className="m-0 ml-4 flex list-none flex-col gap-1 border-l border-(--line) p-0 pl-3 sm:ml-6">
          {node.children.map((c) => (
            <NodeItem key={c.id} node={c} canEdit={canEdit} onAdd={onAdd} onRename={onRename} onMove={onMove} onToggleArchive={onToggleArchive} onDelete={onDelete} />
          ))}
        </ul>
      ) : null}
    </li>
  );
}

/** `/beauty/inventory/categories`: дерево категорій товарів (до 3 рівнів). Зміни - лише за `canEditCatalog` (`inventory.manage`), інакше перегляд. */
export function InventoryCategoriesScreen() {
  const q = useInventoryCategories();
  const m = useCategoryMutations();
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [notEmpty, setNotEmpty] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const flat = q.data ?? [];
  const tree = buildTree(flat);

  function open(d: Dialog) {
    setDialogError(null);
    setNotEmpty(false);
    setMessage(null);
    setError(null);
    setDialog(d);
  }
  function done(text: string) {
    setDialog(null);
    setMessage(text);
  }
  async function run(fn: () => Promise<unknown>, okText: string, inDialog: boolean) {
    setDialogError(null);
    try {
      await fn();
      done(okText);
    } catch (e) {
      if (e instanceof BeautyApiError && e.code === "category_not_empty") setNotEmpty(true);
      const text = humanizeInventoryError(e);
      if (inDialog) setDialogError(text);
      else setError(text);
    }
  }
  async function toggleArchive(n: CategoryNode) {
    setMessage(null);
    setError(null);
    await run(() => (n.isArchived ? m.unarchive.mutateAsync(n.id) : m.archive.mutateAsync(n.id)), n.isArchived ? `Категорію «${n.name}» розархівовано.` : `Категорію «${n.name}» архівовано.`, false);
  }

  return (
    <InventoryPage
      title="Категорії"
      subtitle="Дерево категорій товарів: до 3 рівнів"
      actions={(scope) => (scope.canEditCatalog ? <PrimaryButton onClick={() => open({ kind: "add", parent: null })}>Додати категорію</PrimaryButton> : null)}
    >
      {(scope) => (
        <>
          {message ? <SuccessBanner>{message}</SuccessBanner> : null}
          {error ? <ErrorBanner>{error}</ErrorBanner> : null}
          <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
            <Card>
              {tree.length === 0 ? (
                <EmptyState>{scope.canEditCatalog ? "Категорій ще немає. Додайте першу." : "Категорій ще немає."}</EmptyState>
              ) : (
                <ul aria-label="Дерево категорій" className="m-0 flex list-none flex-col gap-1 p-0">
                  {tree.map((n) => (
                    <NodeItem
                      key={n.id}
                      node={n}
                      canEdit={scope.canEditCatalog}
                      onAdd={(x) => open({ kind: "add", parent: x })}
                      onRename={(x) => open({ kind: "rename", node: x })}
                      onMove={(x) => open({ kind: "move", node: x })}
                      onToggleArchive={(x) => void toggleArchive(x)}
                      onDelete={(x) => open({ kind: "delete", node: x })}
                    />
                  ))}
                </ul>
              )}
            </Card>
          </QueryState>
          {dialog?.kind === "add" ? (
            <NameDialog
              title={dialog.parent ? `Нова підкатегорія в «${dialog.parent.name}»` : "Нова категорія"}
              initial=""
              submitLabel="Додати"
              busy={m.create.isPending}
              error={dialogError}
              onClose={() => setDialog(null)}
              onSubmit={(name) => void run(() => m.create.mutateAsync({ name, parentId: dialog.parent?.id ?? null }), `Категорію «${name}» додано.`, true)}
            />
          ) : null}
          {dialog?.kind === "rename" ? (
            <NameDialog
              title={`Перейменувати «${dialog.node.name}»`}
              initial={dialog.node.name}
              submitLabel="Зберегти"
              busy={m.rename.isPending}
              error={dialogError}
              onClose={() => setDialog(null)}
              onSubmit={(name) => void run(() => m.rename.mutateAsync({ id: dialog.node.id, name }), "Назву збережено.", true)}
            />
          ) : null}
          {dialog?.kind === "move" ? (
            <MoveDialog
              node={dialog.node}
              flat={flat}
              busy={m.move.isPending}
              error={dialogError}
              onClose={() => setDialog(null)}
              onSubmit={(parentId) => void run(() => m.move.mutateAsync({ id: dialog.node.id, parentId }), `Категорію «${dialog.node.name}» переміщено.`, true)}
            />
          ) : null}
          {dialog?.kind === "delete" ? (
            <DeleteDialog
              node={dialog.node}
              busy={m.remove.isPending || m.archive.isPending}
              error={dialogError}
              notEmpty={notEmpty}
              onClose={() => setDialog(null)}
              onDelete={() => void run(() => m.remove.mutateAsync(dialog.node.id), `Категорію «${dialog.node.name}» видалено.`, true)}
              onArchive={() => void run(() => m.archive.mutateAsync(dialog.node.id), `Категорію «${dialog.node.name}» архівовано.`, true)}
            />
          ) : null}
        </>
      )}
    </InventoryPage>
  );
}
