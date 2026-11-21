import type { Metadata } from "next";
import { InventoryOrdersScreen } from "@/features/beauty-admin/components/inventory-orders-screen";

export const metadata: Metadata = { title: "Замовлення товарів" };

export default function Page() {
  return <InventoryOrdersScreen />;
}
