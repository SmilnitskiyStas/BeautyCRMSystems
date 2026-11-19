import type { Metadata } from "next";
import { InventoryMovementsScreen } from "@/features/beauty-admin/components/inventory-movements-screen";

export const metadata: Metadata = { title: "Журнал рухів" };

export default function Page() {
  return <InventoryMovementsScreen />;
}
