import type { Metadata } from "next";
import { CampaignsScreen } from "@/features/beauty-admin/components/campaigns-screen";

export const metadata: Metadata = { title: "Розсилки" };

export default function Page() {
  return <CampaignsScreen />;
}
