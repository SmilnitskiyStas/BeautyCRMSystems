import type { Metadata } from "next";
import { InboxScreen } from "@/features/beauty-admin/components/inbox-screen";

export const metadata: Metadata = { title: "Розмови" };

export default function Page() {
  return <InboxScreen />;
}
