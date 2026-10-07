import type { Metadata } from "next";
import { AnalyticsScreen } from "@/features/beauty-admin/components/analytics-screen";

export const metadata: Metadata = { title: "Аналітика" };

export default function Page() {
  return <AnalyticsScreen />;
}
