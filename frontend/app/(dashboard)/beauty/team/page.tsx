import type { Metadata } from "next";
import { TeamScreen } from "@/features/beauty-admin/components/team-screen";

export const metadata: Metadata = { title: "Команда й доступи" };

export default function Page() {
  return <TeamScreen />;
}
