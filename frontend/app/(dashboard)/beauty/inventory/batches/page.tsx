import type { Metadata } from "next";
import { InventoryBatchesScreen } from "@/features/beauty-admin/components/inventory-batches-screen";

export const metadata: Metadata = { title: "Партії й терміни" };

export default function Page() {
  return <InventoryBatchesScreen />;
}
