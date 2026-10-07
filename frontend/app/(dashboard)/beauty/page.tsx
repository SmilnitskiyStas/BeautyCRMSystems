import type { Metadata } from "next";
import { OverviewScreen } from "@/features/beauty-admin/components/overview-screen";

export const metadata: Metadata = { title: "Огляд" };

export default function Page() {
  return <OverviewScreen />;
}
