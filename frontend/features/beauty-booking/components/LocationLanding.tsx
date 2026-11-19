import Link from "next/link";
import { appDeepLink, formatPublicCode, type PublicLocationRef } from "@/lib/applinks/location-code";

const btnPrimary =
  "flex min-h-12 w-full items-center justify-center rounded-xl bg-[#6D4AFF] px-4 text-base font-medium text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6D4AFF]";
const btnSecondary =
  "flex min-h-12 w-full items-center justify-center rounded-xl border border-[#D9D3E6] bg-white px-4 text-base font-medium text-[#1E1B2E] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6D4AFF]";

const isHttpUrl = (v: string | undefined): v is string => !!v && /^https:\/\//i.test(v.trim());

export interface LocationLandingProps {
  location: PublicLocationRef;
  iosStoreUrl?: string;
  androidStoreUrl?: string;
}

/** Сторінка закладу за посиланням-запрошенням. Кнопка магазину ховається, якщо відповідний env не задано. */
export function LocationLanding({ location, iosStoreUrl, androidStoreUrl }: LocationLandingProps) {
  const addressLine = [location.address, location.city].filter(Boolean).join(", ");
  const ios = isHttpUrl(iosStoreUrl) ? iosStoreUrl.trim() : null;
  const android = isHttpUrl(androidStoreUrl) ? androidStoreUrl.trim() : null;
  return (
    <main className="mx-auto flex max-w-[480px] flex-col gap-6 px-5 py-10 text-[#1E1B2E]">
      <header>
        <p className="text-sm text-[#5E5873]">Вас запрошує заклад</p>
        <h1 className="mt-1 font-[family-name:var(--font-unbounded)] text-xl font-semibold">{location.name}</h1>
        {addressLine ? <p className="mt-2 text-sm text-[#5E5873]">{addressLine}</p> : null}
        <p className="mt-2 text-xs text-[#5E5873]">Код закладу: {formatPublicCode(location.publicCode)}</p>
      </header>
      <div className="flex flex-col gap-3">
        <a href={appDeepLink(location.publicCode)} className={btnPrimary}>
          Відкрити в застосунку
        </a>
        {ios ? (
          <a href={ios} rel="noopener noreferrer" className={btnSecondary}>
            Завантажити в App Store
          </a>
        ) : null}
        {android ? (
          <a href={android} rel="noopener noreferrer" className={btnSecondary}>
            Завантажити в Google Play
          </a>
        ) : null}
        {/* BookingFlow приймає лише tenant; вибір закладу всередині потоку - без змін логіки. */}
        <Link href={`/book/${encodeURIComponent(location.tenantSlug)}`} className={btnSecondary}>
          Записатися онлайн
        </Link>
      </div>
    </main>
  );
}

/** Дружня сторінка для хибного/невідомого коду й тимчасових збоїв. Без деталей причини (немає оракула). */
export function LocationLinkInvalid({ temporary = false }: { temporary?: boolean }) {
  return (
    <main className="mx-auto max-w-[480px] px-5 py-10 text-[#1E1B2E]">
      <h1 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold">
        {temporary ? "Не вдалося завантажити заклад" : "Посилання недійсне"}
      </h1>
      <p className="mt-2 text-sm text-[#5E5873]">
        {temporary
          ? "Спробуйте відкрити посилання ще раз трохи згодом."
          : "Перевірте посилання або попросіть заклад надіслати його ще раз."}
      </p>
    </main>
  );
}
