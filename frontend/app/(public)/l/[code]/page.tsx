import { Suspense } from "react";
import { LocationLanding, LocationLinkInvalid } from "@/features/beauty-booking/components/LocationLanding";
import { normalizePublicCode, resolveLocationCode } from "@/lib/applinks/location-code";

async function Content({ params }: { params: Promise<{ code: string }> }) {
  const { code: raw } = await params;
  let decoded = raw;
  try {
    decoded = decodeURIComponent(raw);
  } catch {
    return <LocationLinkInvalid />;
  }
  const code = normalizePublicCode(decoded);
  if (!code) return <LocationLinkInvalid />;
  const result = await resolveLocationCode(code);
  if (result.status === "not_found") return <LocationLinkInvalid />;
  if (result.status === "error") return <LocationLinkInvalid temporary />;
  return (
    <LocationLanding
      location={result.location}
      iosStoreUrl={process.env.NEXT_PUBLIC_IOS_STORE_URL}
      androidStoreUrl={process.env.NEXT_PUBLIC_ANDROID_STORE_URL}
    />
  );
}

/** Посилання-запрошення закладу: `/l/{publicCode}` (QR, соцмережі). */
export default function LocationLinkPage({ params }: { params: Promise<{ code: string }> }) {
  return (
    <Suspense fallback={<div role="status" className="p-5 text-sm">Завантаження…</div>}>
      <Content params={params} />
    </Suspense>
  );
}
