import type { Metadata } from "next";
import { Suspense } from "react";
import { LocationDetailScreen } from "@/features/beauty-admin/components/location-detail";

export const metadata: Metadata = { title: "Заклад" };

type Params = Promise<{ id: string }>;

async function Content({ params }: { params: Params }) {
  const { id } = await params;
  return <LocationDetailScreen id={id} />;
}

export default function Page({ params }: { params: Params }) {
  return (
    <Suspense fallback={<div role="status" className="text-sm text-(--muted)">Завантаження…</div>}>
      <Content params={params} />
    </Suspense>
  );
}
