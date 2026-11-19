import type { Metadata } from "next";
import { InventoryAlertsScreen } from "@/features/beauty-admin/components/inventory-alerts-screen";

export const metadata: Metadata = { title: "Сповіщення складу" };

export default function Page() {
  return <InventoryAlertsScreen />;
}
