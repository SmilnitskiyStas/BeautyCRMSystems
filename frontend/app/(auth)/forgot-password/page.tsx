import type { Metadata } from "next";
import { ForgotPasswordForm } from "@/features/beauty-auth/components/forgot-password-form";

export const metadata: Metadata = { title: "Скидання пароля", robots: { index: false, follow: false }, referrer: "no-referrer" };

export default function Page() {
  return <ForgotPasswordForm />;
}
