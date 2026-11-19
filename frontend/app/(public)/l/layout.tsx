import type { Metadata } from "next";
import { Onest, Unbounded } from "next/font/google";
import type { ReactNode } from "react";

const onest = Onest({ subsets: ["latin", "cyrillic"], weight: ["400", "500", "600"], variable: "--font-onest" });
const unbounded = Unbounded({ subsets: ["latin", "cyrillic"], weight: ["500", "600"], variable: "--font-unbounded" });

export const metadata: Metadata = {
  title: "Заклад · Beauty CRM",
  description: "Відкрийте заклад у застосунку або запишіться онлайн.",
  robots: { index: false, follow: false },
  referrer: "no-referrer",
};

export default function LocationLinkLayout({ children }: { children: ReactNode }) {
  return (
    <div className={`${onest.variable} ${unbounded.variable} min-h-dvh bg-[#F6F4F8] font-[family-name:var(--font-onest)]`}>
      {children}
    </div>
  );
}
