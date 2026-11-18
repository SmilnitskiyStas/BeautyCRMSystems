"use client";

import { useState, type FormEvent } from "react";
import { useSaveSupplier, useSuppliers } from "../hooks/use-inventory";
import { humanizeInventoryError, toSupplierRequest, validateSupplier, type SupplierErrors } from "../inventory/logic";
import type { Supplier } from "../inventory/types";
import { InputField, InventoryPage, LoadMore, SelectField } from "./inventory-parts";
import { CheckRow, ErrorBanner, SuccessBanner } from "./staff-parts";
import { Badge, Card, EmptyState, PrimaryButton, QueryState, SecondaryButton, Table, TD } from "./ui";

const valuesOf = (s: Supplier | null) => ({
  name: s?.name ?? "",
  contactName: s?.contactName ?? "",
  phone: s?.phone ?? "",
  email: s?.email ?? "",
  note: s?.note ?? "",
  isActive: s?.isActive ?? true,
});

function SupplierForm({ supplier, canEdit, onClose }: { supplier: Supplier | null; canEdit: boolean; onClose: () => void }) {
  const save = useSaveSupplier(supplier?.id ?? null);
  const isEdit = supplier !== null;
  const [values, setValues] = useState(valuesOf(supplier));
  const [errors, setErrors] = useState<SupplierErrors>({});
  const [serverError, setServerError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const set = <K extends keyof typeof values>(k: K, v: (typeof values)[K]) => {
    setValues((cur) => ({ ...cur, [k]: v }));
    setSaved(null);
  };

  async function submit(e: FormEvent) {
    e.preventDefault();
    const next = validateSupplier(values);
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    setServerError(null);
    try {
      await save.mutateAsync(toSupplierRequest(values, isEdit));
      if (isEdit) setSaved("Зміни збережено.");
      else onClose();
    } catch (err) {
      setServerError(humanizeInventoryError(err, "Не вдалося зберегти постачальника. Спробуйте ще раз."));
    }
  }

  return (
    <Card as="div">
      <form onSubmit={(e) => void submit(e)} noValidate className="flex flex-col gap-4" aria-label={isEdit ? `Постачальник «${supplier.name}»` : "Новий постачальник"}>
        <h2 className="text-lg font-semibold">{isEdit ? supplier.name : "Новий постачальник"}</h2>
        <div className="grid gap-4 sm:grid-cols-2">
          <InputField label="Назва" required maxLength={200} value={values.name} onChange={(v) => set("name", v)} error={errors.name} disabled={!canEdit} />
          <InputField label="Контактна особа" maxLength={200} value={values.contactName} onChange={(v) => set("contactName", v)} error={errors.contactName} disabled={!canEdit} />
          <InputField label="Телефон" inputMode="text" maxLength={32} value={values.phone} onChange={(v) => set("phone", v)} error={errors.phone} disabled={!canEdit} />
          <InputField label="Пошта" maxLength={320} value={values.email} onChange={(v) => set("email", v)} error={errors.email} disabled={!canEdit} />
        </div>
        <InputField label="Примітка" maxLength={500} value={values.note} onChange={(v) => set("note", v)} error={errors.note} disabled={!canEdit} />
        {isEdit ? <CheckRow label="Постачальник активний" hint="Неактивний не пропонується в надходженнях." checked={values.isActive} onChange={(on) => set("isActive", on)} disabled={!canEdit} /> : null}
        <div aria-live="polite" className="flex flex-col gap-2">
          {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
          {saved ? <SuccessBanner>{saved}</SuccessBanner> : null}
        </div>
        <div className="flex flex-wrap gap-2">
          {canEdit ? (
            <PrimaryButton type="submit" disabled={save.isPending}>
              {save.isPending ? "Зберігаємо…" : isEdit ? "Зберегти" : "Додати постачальника"}
            </PrimaryButton>
          ) : null}
          <SecondaryButton onClick={onClose}>{isEdit ? "Закрити" : "Скасувати"}</SecondaryButton>
        </div>
      </form>
    </Card>
  );
}

/** `/beauty/inventory/suppliers`: перегляд для `inventory.manage >= own`, зміна - `canEditCatalog`. Контакти не логуються й не показуються без права. */
export function InventorySuppliersScreen() {
  const [search, setSearch] = useState("");
  const [active, setActive] = useState<"true" | "false" | "all">("true");
  const [editing, setEditing] = useState<Supplier | "new" | null>(null);
  const q = useSuppliers({ ...(search.trim() ? { search: search.trim() } : {}), active, take: 50 });

  return (
    <InventoryPage
      title="Постачальники"
      subtitle="Контакти постачальників і прив’язка до товарів"
      requireManage
      actions={(scope) => (scope.canEditCatalog && editing === null ? <PrimaryButton onClick={() => setEditing("new")}>Додати постачальника</PrimaryButton> : null)}
    >
      {(scope) => (
        <>
          {editing ? (
            <SupplierForm key={editing === "new" ? "new" : editing.id} supplier={editing === "new" ? null : editing} canEdit={scope.canEditCatalog} onClose={() => setEditing(null)} />
          ) : null}
          <div className="grid gap-3 sm:grid-cols-2">
            <InputField label="Пошук за назвою" value={search} onChange={setSearch} />
            <SelectField
              label="Статус"
              value={active}
              onChange={(v) => setActive(v as typeof active)}
              options={[
                { value: "true", label: "Активні" },
                { value: "false", label: "Неактивні" },
                { value: "all", label: "Усі" },
              ]}
            />
          </div>
          <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
            {q.items.length === 0 ? (
              <Card>
                <EmptyState>Постачальників за цими умовами немає.</EmptyState>
              </Card>
            ) : (
              <Card>
                <Table headers={["Назва", "Контактна особа", "Телефон", "Пошта", "Товарів", "Статус", "Дії"]} minWidth={820}>
                  {q.items.map((s) => (
                    <tr key={s.id} className="border-t border-(--line)">
                      <th scope="row" className={`${TD} text-left font-semibold`}>
                        {s.name}
                      </th>
                      <td className={TD}>{s.contactName ?? "—"}</td>
                      <td className={TD}>{s.phone ?? "—"}</td>
                      <td className={TD}>{s.email ?? "—"}</td>
                      <td className={TD}>{s.productCount}</td>
                      <td className={TD}>{s.isActive ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}</td>
                      <td className={TD}>
                        <SecondaryButton onClick={() => setEditing(s)} aria-label={`${scope.canEditCatalog ? "Редагувати" : "Відкрити"} постачальника «${s.name}»`}>
                          {scope.canEditCatalog ? "Редагувати" : "Відкрити"}
                        </SecondaryButton>
                      </td>
                    </tr>
                  ))}
                </Table>
                <div className="mt-3">
                  <LoadMore hasMore={q.hasNextPage} loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()} shown={q.items.length} />
                </div>
              </Card>
            )}
          </QueryState>
        </>
      )}
    </InventoryPage>
  );
}
