import { notFound } from "next/navigation";
import { Suspense } from "react";
import { BookingFlow } from "@/features/beauty-booking/components/BookingFlow";
import { isValidSlug } from "@/features/beauty-booking/tenant";

async function Content({ params }: { params: Promise<{ tenant: string }> }) {
  const { tenant } = await params;
  const slug = tenant.toLowerCase();
  if (!isValidSlug(slug)) notFound();
  return <BookingFlow tenant={slug} />;
}

/** Онлайн-запис конкретного закладу: `/book/{tenantSlug}`. */
export default function BookTenantPage({ params }: { params: Promise<{ tenant: string }> }) {
  return (
    <Suspense fallback={<div role="status" className="p-5 text-sm">Завантаження…</div>}>
      <Content params={params} />
    </Suspense>
  );
}
