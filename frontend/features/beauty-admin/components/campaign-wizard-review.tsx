"use client";

import { useId, useRef, useState } from "react";
import { createKeyHolder } from "@/features/beauty-booking/idempotency";
import { dateTimeLabel } from "../format";
import { useCampaignPreview, useConfirmCampaign } from "../hooks/use-marketing";
import { WARNING_TEXT, dmy, formatDiscount, humanizeMarketingError, serviceList } from "../marketing/logic";
import { scheduleFromInput, type WizardForm } from "../marketing/wizard";
import type { CampaignDto, CampaignPreview, PromotionItem } from "../types";
import { AudienceBreakdown } from "./campaign-wizard-steps";
import { ErrorLine } from "./marketing-notices";
import { ModalDialog } from "./modal-dialog";
import { FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

const COUPON_TEXT = { none: "Без купона", personal: "Персональний одноразовий код для кожного клієнта", shared: "Загальний код акції" } as const;

/** Бульбашка Telegram + кнопки, які побачить клієнт (неактивні). */
export function MessagePreview({ text, buttons }: { text: string; buttons: string[] }) {
  return (
    <section aria-label="Так повідомлення побачить клієнт" className="flex flex-col gap-2">
      <div className="text-[13px] font-semibold text-(--muted)">Так повідомлення побачить клієнт у Telegram</div>
      <div data-testid="preview-text" className="max-w-[480px] whitespace-pre-wrap break-words rounded-2xl rounded-bl-sm bg-(--tint) px-4 py-3 text-sm leading-snug">
        {text}
      </div>
      {buttons.length > 0 ? (
        <ul aria-label="Кнопки під повідомленням (неактивні)" className="m-0 flex max-w-[480px] list-none flex-wrap gap-2 p-0">
          {buttons.map((b) => (
            <li key={b} className="cursor-default rounded-full border border-dashed border-(--line-strong) bg-white px-3 py-1.5 text-[13px] text-(--muted) select-none">
              {b}
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}

// ---------- 5. Перегляд ----------

export function StepReview({ campaignId, revision }: { campaignId: string | null; revision: number }) {
  const q = useCampaignPreview(campaignId, revision);
  if (q.isPending) {
    return (
      <div role="status" aria-live="polite" className="text-sm text-(--muted)">
        Готуємо перегляд…
      </div>
    );
  }
  if (q.isError || !q.data) return <ErrorLine error={q.error} fallback="Не вдалося підготувати перегляд." onRetry={() => q.refetch()} />;
  const p = q.data;
  return (
    <div className="flex flex-col gap-4">
      <MessagePreview text={p.sampleText} buttons={p.buttons} />
      <AudienceBreakdown data={p} intervalDays={p.limits.clientIntervalDays} />
      <ul aria-label="Ліміти відправки" className="m-0 flex list-none flex-col gap-1 p-0 text-sm text-(--muted)">
        <li data-testid="limits-line">
          Не більше {p.limits.dailyLimit} повідомлень на добу й не частіше ніж раз на {p.limits.clientIntervalDays} днів для одного клієнта. Орієнтовно відправка займе{" "}
          {p.limits.estimatedDays} дн.
        </li>
        <li data-testid="window-line">
          Надсилаємо лише з {p.window.from} до {p.window.to} за місцевим часом закладу клієнта. Що не встигне цього дня, піде наступного ранку.
        </li>
      </ul>
      {p.warnings.length > 0 ? (
        <ul aria-label="Попередження" className="m-0 flex list-none flex-col gap-2 p-0">
          {p.warnings.map((w) => (
            <li key={w} role="note" className="rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-sm text-[#5C3900]">
              {WARNING_TEXT[w]}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

// ---------- 6. Запуск ----------

export function StepLaunch({
  campaignId,
  revision,
  form,
  promo,
  serviceNames,
  onDone,
  onChanged,
}: {
  campaignId: string;
  revision: number;
  form: WizardForm;
  promo?: PromotionItem;
  serviceNames: string[];
  onDone: (c: CampaignDto) => void;
  /** 409 campaign_changed: майстер повертає на перегляд. */
  onChanged: () => void;
}) {
  const preview = useCampaignPreview(campaignId, revision);
  const [mode, setMode] = useState<"now" | "later">("now");
  const [local, setLocal] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [scheduledIso, setScheduledIso] = useState<string | undefined>(undefined);
  const whenId = useId();
  const data = preview.data;

  function ask() {
    const s = scheduleFromInput(mode, local, Date.now());
    if (s.error) {
      setError(s.error);
      return;
    }
    setError(null);
    setScheduledIso(s.iso);
    setOpen(true);
  }

  if (preview.isPending) return <div role="status" aria-live="polite" className="text-sm text-(--muted)">Завантаження…</div>;
  if (!data) return <ErrorLine error={preview.error} fallback="Не вдалося підготувати запуск." onRetry={() => preview.refetch()} />;

  return (
    <div className="flex flex-col gap-4">
      <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
        <legend className={`${FIELD_LABEL} mb-2 p-0`}>Коли надіслати</legend>
        <label className="flex min-h-11 cursor-pointer items-center gap-3 text-[15px]">
          <input type="radio" name="when" checked={mode === "now"} onChange={() => setMode("now")} />
          Зараз (одразу після підтвердження, у межах вікна {data.window.from}–{data.window.to})
        </label>
        <label className="flex min-h-11 cursor-pointer items-center gap-3 text-[15px]">
          <input type="radio" name="when" checked={mode === "later"} onChange={() => setMode("later")} />
          Запланувати на дату й час
        </label>
        {mode === "later" ? (
          <div className="flex max-w-[320px] flex-col gap-1.5">
            <label htmlFor={whenId} className={FIELD_LABEL}>Дата й час початку</label>
            <input id={whenId} type="datetime-local" value={local} onChange={(e) => setLocal(e.target.value)} className={FIELD_INPUT} />
          </div>
        ) : null}
      </fieldset>
      <p className="m-0 rounded-xl bg-(--page) p-3.5 text-sm">
        Повідомлення надсилаються лише з {data.window.from} до {data.window.to} за місцевим часом закладу клієнта. Якщо ви запускаєте розсилку вночі або ввечері, відправка почнеться наступного ранку.
      </p>
      {error ? <p role="alert" className="m-0 text-sm text-[#8A1F1F]">{error}</p> : null}
      <div>
        <PrimaryButton onClick={ask}>Перевірити й надіслати…</PrimaryButton>
      </div>
      {open ? (
        <ConfirmCampaignDialog
          campaignId={campaignId}
          preview={data}
          scheduledAt={scheduledIso}
          form={form}
          promo={promo}
          serviceNames={serviceNames}
          onClose={() => setOpen(false)}
          onDone={onDone}
          onChanged={() => {
            setOpen(false);
            onChanged();
          }}
        />
      ) : null}
    </div>
  );
}

/**
 * Обов'язковий діалог підтвердження (§26.7, human gate): кількість одержувачів, текст, умови, ревізія з перегляду.
 * `Idempotency-Key` на спробу: повтор того самого підтвердження після збою мережі йде з тим самим ключем.
 */
export function ConfirmCampaignDialog({
  campaignId,
  preview,
  scheduledAt,
  form,
  promo,
  serviceNames,
  onClose,
  onDone,
  onChanged,
}: {
  campaignId: string;
  preview: CampaignPreview;
  scheduledAt?: string;
  form: WizardForm;
  promo?: PromotionItem;
  serviceNames: string[];
  onClose: () => void;
  onDone: (c: CampaignDto) => void;
  onChanged: () => void;
}) {
  const confirm = useConfirmCampaign();
  const keys = useRef(createKeyHolder());
  const [error, setError] = useState<unknown>(null);
  const body = { revision: preview.revision, ...(scheduledAt ? { scheduledAt } : {}) };

  function send() {
    setError(null);
    confirm.mutate(
      { id: campaignId, input: body, key: keys.current.keyFor({ id: campaignId, ...body }) },
      {
        onSuccess: (c) => {
          keys.current.clear();
          onDone(c);
        },
        onError: (e) => {
          if (typeof e === "object" && e && "code" in e && ["campaign_changed", "revision_changed"].includes((e as { code: string }).code)) {
            keys.current.clear();
            onChanged();
            return;
          }
          setError(e);
        },
      },
    );
  }

  return (
    <ModalDialog
      title={`Надіслати ${preview.recipients} клієнтам?`}
      busy={confirm.isPending}
      onClose={onClose}
      actions={
        <>
          <PrimaryButton disabled={confirm.isPending} onClick={send}>
            {confirm.isPending ? "Зачекайте…" : `Надіслати ${preview.recipients} клієнтам`}
          </PrimaryButton>
          <SecondaryButton disabled={confirm.isPending} onClick={onClose}>
            Скасувати
          </SecondaryButton>
        </>
      }
    >
      <p className="m-0 font-semibold">Дію не можна скасувати для вже надісланих повідомлень.</p>
      <dl data-testid="confirm-summary" className="m-0 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5">
        <dt className="text-(--muted)">Одержувачів</dt>
        <dd className="m-0 font-semibold">{preview.recipients}</dd>
        <dt className="text-(--muted)">Версія розсилки</dt>
        <dd className="m-0">{preview.revision}</dd>
        <dt className="text-(--muted)">Коли</dt>
        <dd className="m-0">{scheduledAt ? dateTimeLabel(scheduledAt) : "одразу після підтвердження"}</dd>
        <dt className="text-(--muted)">Вікно відправки</dt>
        <dd className="m-0">
          {preview.window.from}–{preview.window.to} за часом закладу
        </dd>
        {promo ? (
          <>
            <dt className="text-(--muted)">Акція</dt>
            <dd className="m-0">
              {promo.name}: {formatDiscount(promo.discountType, promo.discountValue)} на {serviceList(serviceNames)}
              {promo.endsAt ? `, до ${dmy(promo.endsAt, undefined, true)}` : ""}
            </dd>
          </>
        ) : null}
        <dt className="text-(--muted)">Купон</dt>
        <dd className="m-0">{COUPON_TEXT[form.couponMode]}</dd>
      </dl>
      <div className="text-[13px] font-semibold text-(--muted)">Текст, який отримають клієнти</div>
      <div data-testid="confirm-text" className="max-h-48 overflow-y-auto whitespace-pre-wrap break-words rounded-xl bg-(--tint) p-3 text-sm leading-snug">
        {preview.sampleText}
      </div>
      {error ? (
        <p role="alert" className="m-0 rounded-xl bg-[#FBE4E4] px-3 py-2 text-[#8A1F1F]">
          {humanizeMarketingError(error, "Не вдалося запустити розсилку. Спробуйте ще раз.")}
        </p>
      ) : null}
    </ModalDialog>
  );
}
