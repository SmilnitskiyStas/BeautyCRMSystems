import type { Metadata } from "next";
import { OwnerInviteScreen } from "@/features/beauty-auth/components/owner-invite-screen";

// Токен запрошення живе у фрагменті URL: не віддаємо Referer, не індексуємо (§27.8).
export const metadata: Metadata = {
  title: "Запрошення власника",
  referrer: "no-referrer",
  robots: { index: false, follow: false },
};

export default function Page() {
  return <OwnerInviteScreen />;
}
