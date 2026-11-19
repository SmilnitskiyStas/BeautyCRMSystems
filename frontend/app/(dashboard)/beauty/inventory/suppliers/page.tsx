import type { Metadata } from "next";
import { InventorySuppliersScreen } from "@/features/beauty-admin/components/inventory-suppliers-screen";

export const metadata: Metadata = { title: "Постачальники" };

export default function Page() {
  return <InventorySuppliersScreen />;
}
