import type { Metadata } from "next";
import { ResetPasswordForm } from "@/features/beauty-auth/components/reset-password-form";

export const metadata: Metadata = { title: "Новий пароль", robots: { index: false, follow: false }, referrer: "no-referrer" };

export default function Page() {
  return <ResetPasswordForm />;
}
