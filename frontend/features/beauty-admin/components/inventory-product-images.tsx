"use client";

import { useEffect, useRef, useState, type ChangeEvent } from "react";
import { useInventoryProduct, useProductImageMutations } from "../hooks/use-inventory";
import { IMAGE_ACCEPT, IMAGE_MAX_COUNT, humanizeImageError, moveId, validateImageFile } from "../inventory/images";
import { ErrorBanner, SuccessBanner } from "./staff-parts";
import { PrimaryButton, SecondaryButton } from "./ui";

/** Галерея фото в картці товару (ADR-027, §33.22). Дії (додати, видалити, порядок) - лише за `canEdit` (`canEditCatalog`). */
export function ProductImages({ productId, productName, canEdit }: { productId: string; productName: string; canEdit: boolean }) {
  const detail = useInventoryProduct(productId);
  const { upload, remove, reorder } = useProductImageMutations(productId);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const confirmText = useRef<HTMLParagraphElement>(null);
  const busy = upload.isPending || remove.isPending || reorder.isPending;

  useEffect(() => {
    if (confirmId) confirmText.current?.focus();
  }, [confirmId]);

  if (detail.isPending) return <p role="status" className="text-sm text-(--muted)">Завантажуємо фото…</p>;
  if (detail.isError) return <ErrorBanner>Не вдалося завантажити фото товару.</ErrorBanner>;

  const images = detail.data.images;
  const ids = images.map((i) => i.id);

  async function run(action: () => Promise<unknown>, done: string) {
    setError(null);
    setMessage(null);
    try {
      await action();
      setMessage(done);
    } catch (e) {
      setError(humanizeImageError(e));
    }
  }

  function onPick(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    const problem = validateImageFile(file, images.length);
    if (problem) {
      setMessage(null);
      setError(problem);
      return;
    }
    void run(() => upload.mutateAsync(file), "Фото додано.");
  }

  async function move(index: number, dir: -1 | 1) {
    const name = `Фото ${index + 1}`;
    await run(() => reorder.mutateAsync(moveId(ids, index, dir)), `${name} переміщено на позицію ${index + dir + 1} з ${ids.length}.`);
  }

  async function doRemove(id: string) {
    setConfirmId(null);
    await run(() => remove.mutateAsync(id), "Фото видалено.");
    // Кнопка, що мала фокус, зникла: повертаємо фокус на заголовок галереї.
    heading.current?.focus();
  }

  return (
    <section aria-label="Фото товару" className="flex flex-col gap-3">
      <h3 ref={heading} tabIndex={-1} className="text-base font-semibold outline-none">
        Фото
      </h3>
      <p className="text-[13px] text-(--muted)">
        До {IMAGE_MAX_COUNT} фото, JPEG, PNG або WebP, до 10 МБ. Перше фото показується в списку й каталозі.
      </p>
      {images.length === 0 ? (
        <p className="text-sm text-(--muted)">Фото ще немає.</p>
      ) : (
        <ul className="m-0 flex list-none flex-wrap gap-3 p-0">
          {images.map((img, i) => (
            <li key={img.id} className="flex w-32 flex-col gap-2 rounded-xl border border-(--line-strong) p-2">
              {/* eslint-disable-next-line @next/next/no-img-element -- зовнішній URL із DTO, без next/image-домену */}
              <img src={img.thumbUrl} alt={`Фото ${i + 1} з ${images.length} товару «${productName}»`} width={112} height={112} loading="lazy" className="h-28 w-28 rounded-lg bg-(--line) object-cover" />
              {i === 0 ? <span className="text-[12px] text-(--muted)">Головне</span> : null}
              {canEdit ? (
                <div className="flex flex-wrap gap-1">
                  <SecondaryButton disabled={busy || i === 0} onClick={() => void move(i, -1)} aria-label={`Фото ${i + 1}: перемістити вгору`}>
                    ↑
                  </SecondaryButton>
                  <SecondaryButton disabled={busy || i === images.length - 1} onClick={() => void move(i, 1)} aria-label={`Фото ${i + 1}: перемістити вниз`}>
                    ↓
                  </SecondaryButton>
                  <SecondaryButton disabled={busy} onClick={() => setConfirmId(img.id)} aria-label={`Видалити фото ${i + 1}`}>
                    Видалити
                  </SecondaryButton>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {canEdit && confirmId ? (
        <div role="group" aria-labelledby="confirm-img-t" className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]">
          <p id="confirm-img-t" ref={confirmText} tabIndex={-1} className="text-sm font-semibold outline-none">
            Видалити фото {ids.indexOf(confirmId) + 1}? Дію не можна скасувати.
          </p>
          <div className="flex flex-wrap gap-2">
            <PrimaryButton disabled={busy} onClick={() => void doRemove(confirmId)}>
              Так, видалити
            </PrimaryButton>
            <SecondaryButton onClick={() => setConfirmId(null)}>Скасувати</SecondaryButton>
          </div>
        </div>
      ) : null}
      {canEdit ? (
        <div className="flex flex-wrap items-center gap-2">
          <input ref={fileInput} type="file" accept={IMAGE_ACCEPT} hidden tabIndex={-1} onChange={onPick} data-testid="product-image-input" />
          <SecondaryButton disabled={busy || images.length >= IMAGE_MAX_COUNT} onClick={() => fileInput.current?.click()}>
            {upload.isPending ? "Завантажуємо…" : "Додати фото"}
          </SecondaryButton>
        </div>
      ) : null}
      <div aria-live="polite">
        {message ? <SuccessBanner>{message}</SuccessBanner> : null}
        {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      </div>
    </section>
  );
}
