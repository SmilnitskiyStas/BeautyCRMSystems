import type { Metadata } from "next";
import { Suspense } from "react";
import { AcceptInviteForm } from "@/features/beauty-auth/components/accept-invite-form";

export const metadata: Metadata = { title: "Запрошення", referrer: "no-referrer" };

async function InviteContent({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return <AcceptInviteForm token={token} />;
}

export default function Page({ params }: { params: Promise<{ token: string }> }) {
  return (
    <Suspense fallback={null}>
      <InviteContent params={params} />
    </Suspense>
  );
}
