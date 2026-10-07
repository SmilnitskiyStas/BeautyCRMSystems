import type { Metadata } from "next";
import { PromosScreen } from "@/features/beauty-admin/components/promos-screen";

export const metadata: Metadata = { title: "Ціни та акції" };

export default function Page() {
  return <PromosScreen />;
}
