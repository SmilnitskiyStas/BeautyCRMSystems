import type { Metadata } from "next";
import { InventoryStockScreen } from "@/features/beauty-admin/components/inventory-stock-screen";

export const metadata: Metadata = { title: "Склад" };

export default function Page() {
  return <InventoryStockScreen />;
}
