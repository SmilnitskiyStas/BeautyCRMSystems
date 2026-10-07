import type { Metadata } from "next";
import { SettingsScreen } from "@/features/beauty-admin/components/settings-screen";

export const metadata: Metadata = { title: "Налаштування" };

export default function Page() {
  return <SettingsScreen />;
}
