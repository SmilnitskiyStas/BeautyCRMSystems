import type { Metadata } from "next";
import { Suspense } from "react";
import { PromosScreen } from "@/features/beauty-admin/components/promos-screen";

export const metadata: Metadata = { title: "Ціни та акції" };

export default function Page() {
  return (
    <Suspense fallback={<div role="status" className="text-sm text-(--muted)">Завантаження…</div>}>
      <PromosScreen />
    </Suspense>
  );
}
