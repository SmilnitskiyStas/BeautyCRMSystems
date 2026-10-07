import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { Suspense } from "react";
import { LoginForm } from "@/features/beauty-auth/components/login-form";
import { USE_MOCK } from "@/features/beauty-auth/types";

export const metadata: Metadata = { title: "Вхід" };

export default function Page() {
  // Демо-режим без backend: вхід не потрібен.
  if (USE_MOCK) redirect("/beauty");
  return (
    <Suspense fallback={null}>
      <LoginForm />
    </Suspense>
  );
}
