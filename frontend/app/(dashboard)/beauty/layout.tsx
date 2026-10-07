import type { Metadata } from "next";
import { Onest, Unbounded } from "next/font/google";
import type { ReactNode } from "react";
import { AdminShell } from "@/features/beauty-admin/components/admin-shell";
import "./beauty.css";

const onest = Onest({ subsets: ["latin", "cyrillic"], variable: "--font-onest", display: "swap" });
const unbounded = Unbounded({ subsets: ["latin", "cyrillic"], variable: "--font-unbounded", display: "swap" });

export const metadata: Metadata = {
  title: { default: "Beauty CRM", template: "%s · Beauty CRM" },
  robots: { index: false, follow: false },
};

export default function BeautyLayout({ children }: { children: ReactNode }) {
  return (
    <div lang="uk" className={`beauty-admin ${onest.variable} ${unbounded.variable} min-h-screen`}>
      <AdminShell>{children}</AdminShell>
    </div>
  );
}
