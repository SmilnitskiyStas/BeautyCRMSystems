import type { Metadata } from "next";
import { Suspense } from "react";
import { AppointmentView } from "@/features/beauty-booking/components/AppointmentView";
import { resolveTenant } from "@/features/beauty-booking/tenant";

// Сторінка з приватним токеном у шляху: не індексувати.
export const metadata: Metadata = { title: "Ваш запис", robots: { index: false, follow: false } };

type Props = {
  params: Promise<{ token: string }>;
  searchParams: Promise<{ tenant?: string | string[] }>;
};

async function AppointmentContent({ params, searchParams }: Props) {
  const [{ token }, sp] = await Promise.all([params, searchParams]);
  const tenant = resolveTenant(Array.isArray(sp.tenant) ? sp.tenant[0] : sp.tenant);
  if (!tenant) {
    return <p role="alert" className="text-sm text-[#A2300A]">Не вказано заклад. Відкрийте посилання з підтвердження запису.</p>;
  }
  return <AppointmentView token={token} tenant={tenant} />;
}

export default function AppointmentPage(props: Props) {
  return (
    <main className="mx-auto flex min-h-dvh w-full max-w-[480px] flex-col gap-3 px-5 py-6 text-[#1E1B2E]">
      <h1 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold">Ваш запис</h1>
      <Suspense fallback={<div role="status" className="text-sm">Завантаження…</div>}>
        <AppointmentContent {...props} />
      </Suspense>
    </main>
  );
}
