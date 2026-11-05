"use client";

import { useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canUseMarketing } from "@/features/beauty-auth/permissions";
import { usePriceList } from "../hooks/use-beauty-admin";
import { useDeleteCampaign, useMarketingAvailability, usePromotions, useSaveCampaign, useSharedCoupons } from "../hooks/use-marketing";
import { humanizeMarketingError, isMarketingDisabled } from "../marketing/logic";
import { emptyForm, formFromDto, retemplate, toCampaignRequest, validateStep, type WizardForm } from "../marketing/wizard";
import type { CampaignDto } from "../types";
import { StepLaunch, StepReview } from "./campaign-wizard-review";
import { StepAudience, StepCoupon, StepPromo, StepText, type SampleInfo } from "./campaign-wizard-steps";
import { ErrorLine, MarketingDisabledNotice, MarketingForbiddenNotice } from "./marketing-notices";
import { ModalDialog } from "./modal-dialog";
import { Card, PageHeader, PrimaryButton, QueryState, SecondaryButton, TextLink } from "./ui";

const STEPS = ["Акція", "Аудиторія", "Текст", "Купон", "Перегляд", "Запуск"] as const;

/**
 * Майстер розсилки (§26.12): акція -> аудиторія з переглядом -> текст -> купон -> перегляд -> запуск з обов'язковим підтвердженням.
 * Чернетка зберігається при переході до перегляду (кожне збереження збільшує `revision`).
 */
export function CampaignWizard({ campaign, presetPromotionId }: { campaign?: CampaignDto; presetPromotionId?: string }) {
  const { user } = useAuth();
  const allowed = canUseMarketing(user.role);
  const router = useRouter();
  const availability = useMarketingAvailability(allowed);
  const promos = usePromotions(allowed);
  const services = usePriceList();

  const [form, setForm] = useState<WizardForm>(() => (campaign ? formFromDto(campaign) : emptyForm()));
  const [step, setStep] = useState(1);
  const [campaignId, setCampaignId] = useState<string | null>(campaign?.id ?? null);
  const [revision, setRevision] = useState(campaign?.revision ?? 0);
  const [problem, setProblem] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [askDelete, setAskDelete] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const save = useSaveCampaign();
  const del = useDeleteCampaign();
  const [presetApplied, setPresetApplied] = useState(false);

  const promotions = useMemo(() => promos.data ?? [], [promos.data]);
  const promo = promotions.find((p) => p.id === form.promotionId);
  const sharedCoupons = useSharedCoupons(promo?.id, allowed && form.couponMode === "shared" && !!promo);
  const sharedCode = sharedCoupons.data?.find((c) => c.id === form.sharedCouponId)?.code;
  const serviceNames = useMemo(
    () => (promo ? promo.serviceIds.map((id) => services.data?.find((s) => s.serviceId === id)?.name).filter((n): n is string => !!n) : []),
    [promo, services.data],
  );
  const info: SampleInfo = { promo, serviceNames, sharedCode };

  // Попередньо вибрана акція (з «Ціни та акції» / ШІ): один раз, коли список акцій завантажено.
  // (коригування стану під час рендера: один раз, коли список акцій завантажено)
  if (!campaign && presetPromotionId && !presetApplied && promos.data) {
    setPresetApplied(true);
    const p = promos.data.find((x) => x.id === presetPromotionId);
    if (p) {
      const base = emptyForm();
      setForm(retemplate(base, { ...base, type: "promo", promotionId: p.id, name: `Розсилка: ${p.name}`, couponMode: p.access === "code_only" ? "personal" : "none" }));
    }
  }

  useEffect(() => heading.current?.focus(), [step]);

  if (!allowed) return <MarketingForbiddenNotice />;
  if (availability.disabled || isMarketingDisabled(promos.error)) {
    return (
      <>
        <TextLink href="/beauty/campaigns">← Усі розсилки</TextLink>
        <MarketingDisabledNotice />
      </>
    );
  }

  const stepError = (n: number) => validateStep(n, form, promo, sharedCoupons.data);

  async function next() {
    setNotice(null);
    const err = stepError(step);
    if (err) {
      setProblem(err);
      return;
    }
    setProblem(null);
    if (step === 4) {
      try {
        const saved = await save.mutateAsync({ id: campaignId, request: toCampaignRequest(form) });
        setCampaignId(saved.id);
        setRevision(saved.revision);
      } catch (e) {
        setProblem(humanizeMarketingError(e, "Не вдалося зберегти чернетку."));
        return;
      }
    }
    setStep((s) => Math.min(6, s + 1));
  }

  const back = () => {
    setProblem(null);
    setStep((s) => Math.max(1, s - 1));
  };

  return (
    <>
      <TextLink href="/beauty/campaigns">← Усі розсилки</TextLink>
      <PageHeader title={campaign ? "Редагування розсилки" : "Нова розсилка"} subtitle="Розсилка піде лише клієнтам зі згодою і лише після вашого підтвердження." />
      <nav aria-label="Кроки розсилки">
        <ol className="m-0 flex list-none flex-wrap gap-2 p-0">
          {STEPS.map((s, i) => (
            <li
              key={s}
              aria-current={step === i + 1 ? "step" : undefined}
              className={`rounded-full px-3 py-1.5 text-[13px] font-semibold ${step === i + 1 ? "bg-(--accent) text-white" : step > i + 1 ? "bg-(--tint) text-(--ink)" : "bg-white text-(--muted)"}`}
            >
              {i + 1}. {s}
            </li>
          ))}
        </ol>
      </nav>

      <Card className="flex flex-col gap-4">
        <h2 ref={heading} tabIndex={-1} className="text-lg font-semibold outline-none">
          Крок {step} з {STEPS.length}: {STEPS[step - 1]}
        </h2>
        {notice ? (
          <p role="status" data-testid="wizard-notice" className="m-0 rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-sm text-[#5C3900]">{notice}</p>
        ) : null}

        {step === 1 ? (
          <QueryState isPending={promos.isPending} isError={promos.isError} onRetry={() => promos.refetch()}>
            <StepPromo form={form} setForm={setForm} promotions={promotions} />
          </QueryState>
        ) : null}
        {step === 2 ? <StepAudience form={form} setForm={setForm} invalid={stepError(2)} /> : null}
        {step === 3 ? <StepText form={form} setForm={setForm} info={info} /> : null}
        {step === 4 ? <StepCoupon form={form} setForm={setForm} promo={promo} /> : null}
        {step === 5 ? <StepReview campaignId={campaignId} revision={revision} /> : null}
        {step === 6 && campaignId ? (
          <StepLaunch
            campaignId={campaignId}
            revision={revision}
            form={form}
            promo={promo}
            serviceNames={serviceNames}
            onDone={(c) => router.push(`/beauty/campaigns/${c.id}`)}
            onChanged={() => {
              setStep(5);
              setNotice("Кампанію змінили, поки ви переглядали. Перегляд оновлено: перевірте його ще раз і запустіть знову.");
            }}
          />
        ) : null}

        {problem ? <ErrorLine error={problem} /> : null}
        {save.isError && !problem ? <ErrorLine error={save.error} /> : null}

        <div className="flex flex-wrap gap-2 border-t border-(--line) pt-4">
          {step > 1 ? <SecondaryButton onClick={back}>Назад</SecondaryButton> : null}
          {step < 6 ? (
            <PrimaryButton disabled={save.isPending} onClick={() => void next()}>
              {save.isPending ? "Зберігаємо…" : step === 4 ? "Зберегти й переглянути" : "Далі"}
            </PrimaryButton>
          ) : null}
          {campaignId ? <SecondaryButton onClick={() => setAskDelete(true)}>Видалити чернетку</SecondaryButton> : null}
        </div>
      </Card>

      {askDelete && campaignId ? (
        <ModalDialog
          title="Видалити чернетку?"
          busy={del.isPending}
          onClose={() => setAskDelete(false)}
          actions={
            <>
              <PrimaryButton
                disabled={del.isPending}
                onClick={() => del.mutate(campaignId, { onSuccess: () => router.push("/beauty/campaigns"), onError: () => setAskDelete(false) })}
              >
                {del.isPending ? "Видаляємо…" : "Видалити"}
              </PrimaryButton>
              <SecondaryButton disabled={del.isPending} onClick={() => setAskDelete(false)}>Скасувати</SecondaryButton>
            </>
          }
        >
          <p className="m-0">Чернетку буде видалено безповоротно. Нічого не надсилалося.</p>
        </ModalDialog>
      ) : null}
    </>
  );
}
