import type { Metadata } from "next";
import { Suspense } from "react";
import { CampaignDetailScreen } from "@/features/beauty-admin/components/campaign-detail";

export const metadata: Metadata = { title: "Розсилка" };

type Params = Promise<{ id: string }>;

async function Content({ params }: { params: Params }) {
  const { id } = await params;
  return <CampaignDetailScreen id={id} />;
}

export default function Page({ params }: { params: Params }) {
  return (
    <Suspense fallback={<div role="status" className="text-sm text-(--muted)">Завантаження…</div>}>
      <Content params={params} />
    </Suspense>
  );
}
