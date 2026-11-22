"use client";

import { useRef, useState, type ChangeEvent } from "react";
import { useLocationLogoMutations } from "../hooks/use-beauty-admin";
import { IMAGE_ACCEPT, humanizeLogoError, validateImageFile } from "../inventory/images";
import type { BeautyLocation } from "../types";
import { ErrorBanner, SuccessBanner } from "./staff-parts";
import { SecondaryButton } from "./ui";

/** Ініціали закладу (до двох літер) для заглушки без логотипу. */
export function locationInitials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  const letters = words.length > 1 ? words.slice(0, 2).map((w) => Array.from(w)[0]!) : Array.from(words[0] ?? "").slice(0, 2);
  return letters.join("").toUpperCase() || "?";
}

/** Квадратний логотип закладу або ініціали; декоративний поруч з назвою (`alt=""`). */
export function LocationAvatar({ location, size = 40 }: { location: Pick<BeautyLocation, "name" | "logoUrl">; size?: number }) {
  const box = { width: size, height: size };
  const cls = "flex-none rounded-xl";
  if (location.logoUrl) {
    // eslint-disable-next-line @next/next/no-img-element -- зовнішній URL із DTO, без next/image-домену
    return <img src={location.logoUrl} alt="" width={size} height={size} loading="lazy" style={box} className={`${cls} bg-(--line) object-cover`} />;
  }
  return (
    <span aria-hidden="true" style={{ ...box, fontSize: Math.round(size * 0.38) }} className={`${cls} inline-flex items-center justify-center bg-(--line) font-semibold text-(--muted)`}>
      {locationInitials(location.name)}
    </span>
  );
}

/** Блок «Логотип» у картці закладу. Кнопки - лише за `canEdit` (owner/admin). */
export function LocationLogoBlock({ location, canEdit }: { location: BeautyLocation; canEdit: boolean }) {
  const { upload, remove } = useLocationLogoMutations(location.id);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const busy = upload.isPending || remove.isPending;
  const has = !!location.logoUrl;

  async function run(action: () => Promise<unknown>, done: string) {
    setError(null);
    setMessage(null);
    try {
      await action();
      setMessage(done);
    } catch (e) {
      setError(humanizeLogoError(e));
    }
  }

  function onPick(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    const problem = validateImageFile(file, 0);
    if (problem) {
      setMessage(null);
      setError(problem);
      return;
    }
    void run(() => upload.mutateAsync(file), has ? "Логотип замінено." : "Логотип завантажено.");
  }

  return (
    <section aria-label="Логотип закладу" className="flex min-w-0 flex-col gap-3">
      <h3 className="text-base font-semibold">Логотип</h3>
      <div className="flex flex-wrap items-center gap-4">
        {location.logoUrl ? (
          // eslint-disable-next-line @next/next/no-img-element -- зовнішній URL із DTO, без next/image-домену
          <img src={location.logoUrl} alt={`Логотип закладу «${location.name}»`} width={96} height={96} className="h-24 w-24 rounded-2xl bg-(--line) object-cover" />
        ) : (
          <span role="img" aria-label={`Логотипу немає, ініціали закладу «${location.name}»`} className="inline-flex h-24 w-24 items-center justify-center rounded-2xl bg-(--line) text-2xl font-semibold text-(--muted)">
            {locationInitials(location.name)}
          </span>
        )}
        {canEdit ? (
          <div className="flex flex-wrap items-center gap-2">
            <input ref={fileInput} type="file" accept={IMAGE_ACCEPT} hidden tabIndex={-1} onChange={onPick} data-testid="location-logo-input" />
            <SecondaryButton disabled={busy} onClick={() => fileInput.current?.click()}>
              {upload.isPending ? "Завантажуємо…" : has ? "Замінити" : "Завантажити логотип"}
            </SecondaryButton>
            {has ? (
              <SecondaryButton disabled={busy} onClick={() => void run(() => remove.mutateAsync(), "Логотип видалено.")}>
                {remove.isPending ? "Видаляємо…" : "Видалити"}
              </SecondaryButton>
            ) : null}
          </div>
        ) : null}
      </div>
      {canEdit ? <p className="text-[13px] text-(--muted)">JPEG, PNG або WebP, до 10 МБ. Краще квадратне зображення.</p> : null}
      <div aria-live="polite">
        {message ? <SuccessBanner>{message}</SuccessBanner> : null}
        {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      </div>
    </section>
  );
}
