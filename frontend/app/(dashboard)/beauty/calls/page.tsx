import type { Metadata } from "next";
import { CallsScreen } from "@/features/beauty-admin/components/calls-screen";

export const metadata: Metadata = { title: "Дзвінки" };

export default function Page() {
  return <CallsScreen />;
}
