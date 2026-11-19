"use client";

import { useState } from "react";
import { useInventoryCategories, useInventoryProducts } from "../hooks/use-inventory";
import { PURPOSE_LABEL, formatQuantity, formatMoney, unitShort } from "../inventory/logic";
import type { Product, ProductPurpose } from "../inventory/types";
import { InputField, InventoryPage, LoadMore, SelectField } from "./inventory-parts";
import { ProductForm } from "./inventory-product-form";
import { Badge, Card, EmptyState, PrimaryButton, QueryState, SecondaryButton, Table, TD } from "./ui";

/** `/beauty/inventory/products`: каталог товарів. Створення/зміна - за `canEditCatalog`; налаштування по закладах - за `canManage`. */
export function InventoryProductsScreen() {
  const [search, setSearch] = useState("");
  const [category, setCategory] = useState("");
  const [purpose, setPurpose] = useState("");
  const [active, setActive] = useState<"true" | "false" | "all">("true");
  const [editing, setEditing] = useState<Product | "new" | null>(null);

  const q = useInventoryProducts({
    ...(search.trim() ? { search: search.trim() } : {}),
    ...(category ? { category } : {}),
    ...(purpose ? { purpose: purpose as ProductPurpose } : {}),
    active,
    take: 50,
  });
  const categories = useInventoryCategories();

  return (
    <InventoryPage
      title="Товари"
      subtitle="Каталог витратних матеріалів і товарів для продажу"
      actions={(scope) => (scope.canEditCatalog && editing === null ? <PrimaryButton onClick={() => setEditing("new")}>Додати товар</PrimaryButton> : null)}
    >
      {(scope) => (
        <>
          {editing ? (
            <ProductForm key={editing === "new" ? "new" : editing.id} product={editing === "new" ? null : editing} scope={scope} onClose={() => setEditing(null)} />
          ) : null}
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <InputField label="Пошук за назвою чи штрихкодом" value={search} onChange={setSearch} />
            <SelectField label="Категорія" value={category} onChange={setCategory} placeholder="Усі категорії" options={(categories.data ?? []).map((c) => ({ value: c, label: c }))} />
            <SelectField
              label="Призначення"
              value={purpose}
              onChange={setPurpose}
              placeholder="Будь-яке"
              options={(Object.keys(PURPOSE_LABEL) as ProductPurpose[]).map((p) => ({ value: p, label: PURPOSE_LABEL[p] }))}
            />
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
                <EmptyState>{scope.canEditCatalog ? "Товарів ще немає. Додайте перший." : "Товарів за цими умовами немає."}</EmptyState>
              </Card>
            ) : (
              <Card>
                <Table
                  caption="Товари"
                  headers={["Товар", "Категорія", "Од.", "Штрихкод", "Постачальник", "Призначення", ...(scope.costs !== "none" ? ["Закупівельна ціна"] : []), "Мін. залишок", "Статус", "Дії"]}
                  minWidth={980}
                >
                  {q.items.map((p) => (
                    <tr key={p.id} className="border-t border-(--line)">
                      <th scope="row" className={`${TD} text-left font-semibold`}>
                        {p.name}
                        {p.trackExpiry ? <span className="block text-[13px] font-normal text-(--muted)">Зі строком придатності</span> : null}
                      </th>
                      <td className={TD}>{p.category ?? "—"}</td>
                      <td className={TD}>{unitShort(p.unit)}</td>
                      <td className={TD}>{p.barcode ?? "—"}</td>
                      <td className={TD}>{p.supplier?.name ?? "—"}</td>
                      <td className={TD}>{PURPOSE_LABEL[p.purpose]}</td>
                      {scope.costs !== "none" ? <td className={TD}>{p.purchasePrice === null || p.purchasePrice === undefined ? "—" : formatMoney(p.purchasePrice)}</td> : null}
                      <td className={TD}>{p.minStock !== null && p.minStock > 0 ? formatQuantity(p.minStock, p.unit) : "—"}</td>
                      <td className={TD}>{p.isActive ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}</td>
                      <td className={TD}>
                        <SecondaryButton onClick={() => setEditing(p)} aria-label={`${scope.canEditCatalog ? "Редагувати" : "Відкрити"} товар «${p.name}»`}>
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
