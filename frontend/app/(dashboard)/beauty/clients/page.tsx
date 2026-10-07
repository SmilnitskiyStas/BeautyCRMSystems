import type { Metadata } from "next";
import { ClientsListScreen } from "@/features/beauty-admin/components/clients-screens";

export const metadata: Metadata = { title: "Клієнти" };

export default function Page() {
  return <ClientsListScreen />;
}
