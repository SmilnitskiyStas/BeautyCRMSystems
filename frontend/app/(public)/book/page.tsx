import { BookingFlow } from "@/features/beauty-booking/components/BookingFlow";
import { ENV_TENANT } from "@/features/beauty-booking/tenant";

/** Заклад за замовчуванням - `NEXT_PUBLIC_TENANT_SLUG`; для іншого використовуйте `/book/{slug}`. */
export default function BookPage() {
  if (!ENV_TENANT) {
    return (
      <main className="mx-auto max-w-[480px] px-5 py-10 text-[#1E1B2E]">
        <h1 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold">Онлайн-запис недоступний</h1>
        <p className="mt-2 text-sm text-[#5E5873]">Не вказано заклад. Скористайтеся посиланням від вашого салону.</p>
      </main>
    );
  }
  return <BookingFlow tenant={ENV_TENANT} />;
}
