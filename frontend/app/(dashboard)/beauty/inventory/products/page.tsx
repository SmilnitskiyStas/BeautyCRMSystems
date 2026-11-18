import type { Metadata } from "next";
import { InventoryProductsScreen } from "@/features/beauty-admin/components/inventory-products-screen";

export const metadata: Metadata = { title: "Товари складу" };

export default function Page() {
  return <InventoryProductsScreen />;
}
