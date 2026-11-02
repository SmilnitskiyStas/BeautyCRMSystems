import type { Metadata } from "next";
import { Suspense } from "react";
import { CampaignWizard } from "@/features/beauty-admin/components/campaign-wizard";

export const metadata: Metadata = { title: "Нова розсилка" };

type SearchParams = Promise<{ promotionId?: string | string[] }>;

async function Content({ searchParams }: { searchParams: SearchParams }) {
  const { promotionId } = await searchParams;
  return <CampaignWizard presetPromotionId={Array.isArray(promotionId) ? promotionId[0] : promotionId} />;
}

export default function Page({ searchParams }: { searchParams: SearchParams }) {
  return (
    <Suspense fallback={<div role="status" className="text-sm text-(--muted)">Завантаження…</div>}>
      <Content searchParams={searchParams} />
    </Suspense>
  );
}
