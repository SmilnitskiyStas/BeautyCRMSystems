import type { Metadata } from "next";
import { InventoryCategoriesScreen } from "@/features/beauty-admin/components/inventory-categories-screen";

export const metadata: Metadata = { title: "Категорії" };

export default function Page() {
  return <InventoryCategoriesScreen />;
}
