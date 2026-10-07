import type { Metadata } from "next";
import { CalendarScreen } from "@/features/beauty-admin/components/calendar-screen";

export const metadata: Metadata = { title: "Записи" };

export default function Page() {
  return <CalendarScreen />;
}
