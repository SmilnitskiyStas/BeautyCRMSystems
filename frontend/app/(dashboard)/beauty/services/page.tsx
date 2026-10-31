import type { Metadata } from "next";
import { ServicesScreen } from "@/features/beauty-admin/components/services-screen";

export const metadata: Metadata = { title: "Послуги" };

export default function Page() {
  return <ServicesScreen />;
}
