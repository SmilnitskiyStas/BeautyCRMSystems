import type { Metadata } from "next";
import { InventoryStocktakesScreen } from "@/features/beauty-admin/components/inventory-stocktakes-screen";

export const metadata: Metadata = { title: "Інвентаризації" };

export default function Page() {
  return <InventoryStocktakesScreen />;
}
