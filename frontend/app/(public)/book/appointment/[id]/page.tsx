import { Suspense } from "react";
import { AppointmentView } from "@/features/beauty-booking/components/AppointmentView";

async function AppointmentContent({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <AppointmentView id={id} />;
}

export default function AppointmentPage({ params }: { params: Promise<{ id: string }> }) {
  return (
    <main className="mx-auto flex min-h-dvh w-full max-w-[480px] flex-col gap-3 px-5 py-6 text-[#1E1B2E]">
      <h1 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold">Ваш запис</h1>
      <Suspense fallback={<div role="status" className="text-sm">Завантаження…</div>}>
        <AppointmentContent params={params} />
      </Suspense>
    </main>
  );
}
