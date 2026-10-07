import type { Metadata } from "next";
import { ChannelsScreen } from "@/features/beauty-admin/components/channels-screen";

export const metadata: Metadata = { title: "Канали" };

export default function Page() {
  return <ChannelsScreen />;
}
