import type { Metadata } from "next";
import { TelegramNotificationsScreen } from "@/features/beauty-admin/components/telegram-notifications-screen";

export const metadata: Metadata = { title: "Сповіщення" };

export default function Page() {
  return <TelegramNotificationsScreen />;
}
