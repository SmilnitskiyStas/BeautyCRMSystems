import type { Metadata } from "next";
import { Suspense } from "react";
import { ClientProfileScreen } from "@/features/beauty-admin/components/clients-screens";

export const metadata: Metadata = { title: "Профіль клієнта" };

type Params = Promise<{ id: string }>;

async function Content({ params }: { params: Params }) {
  const { id } = await params;
  return <ClientProfileScreen id={id} />;
}

export default function Page({ params }: { params: Params }) {
  return (
    <Suspense fallback={<div role="status" className="text-sm text-(--muted)">Завантаження…</div>}>
      <Content params={params} />
    </Suspense>
  );
}
