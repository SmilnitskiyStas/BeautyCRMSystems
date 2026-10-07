import type { Metadata } from "next";
import { AiScreen } from "@/features/beauty-admin/components/ai-screen";

export const metadata: Metadata = { title: "AI-асистент" };

export default function Page() {
  return <AiScreen />;
}
