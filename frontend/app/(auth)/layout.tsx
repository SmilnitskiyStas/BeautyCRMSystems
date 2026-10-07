import type { Metadata } from "next";
import { Onest, Unbounded } from "next/font/google";
import type { ReactNode } from "react";
import "../(dashboard)/beauty/beauty.css";

const onest = Onest({ subsets: ["latin", "cyrillic"], variable: "--font-onest", display: "swap" });
const unbounded = Unbounded({ subsets: ["latin", "cyrillic"], variable: "--font-unbounded", display: "swap" });

export const metadata: Metadata = {
  title: { default: "Вхід", template: "%s · Beauty CRM" },
  robots: { index: false, follow: false },
};

export default function AuthLayout({ children }: { children: ReactNode }) {
  return (
    <div lang="uk" className={`beauty-admin ${onest.variable} ${unbounded.variable} min-h-screen`}>
      {children}
    </div>
  );
}
