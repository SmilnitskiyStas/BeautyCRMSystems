"use client";

import Link from "next/link";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canManageCatalog } from "@/features/beauty-auth/permissions";
import { useLocations, useServices, useStaff } from "../hooks/use-beauty-admin";
import { Card } from "./ui";

/** «З чого почати» (owner/admin): кроки початкового налаштування зі станом; зникає, коли всі виконано. */
export function OnboardingChecklist() {
  const { user } = useAuth();
  const allowed = user ? canManageCatalog(user.role) : false;
  const locations = useLocations(allowed);
  const staff = useStaff(allowed);
  const services = useServices(allowed);

  if (!allowed) return null;
  // Поки дані не завантажені (або запит впав) - нічого не показуємо, щоб не блимати хибним «не виконано».
  if (!locations.data || !staff.data || !services.data) return null;

  const steps = [
    { id: "locations", done: locations.data.length > 0, title: "Створіть заклад", action: "Перейти до закладів", href: "/beauty/locations" },
    { id: "staff", done: staff.data.length > 0, title: "Додайте спеціалістів", action: "Перейти до спеціалістів", href: "/beauty/staff" },
    { id: "services", done: services.data.length > 0, title: "Додайте послуги й ціни", action: "Перейти до послуг", href: "/beauty/services" },
  ];
  if (steps.every((s) => s.done)) return null;
  const doneCount = steps.filter((s) => s.done).length;

  return (
    <Card as="section" className="flex flex-col gap-3">
        <h2 id="onboarding-title" className="text-lg font-semibold">
          З чого почати
        </h2>
        <p className="text-sm text-(--muted)">
          Виконано кроків: {doneCount} з {steps.length}
        </p>
        <ol className="flex flex-col">
          {steps.map((s, i) => (
            <li key={s.id} data-done={s.done} className="flex flex-wrap items-center gap-3 border-t border-(--line) py-2">
              <span className="min-w-0 flex-1 text-[15px] font-semibold">
                {i + 1}. {s.title}
                <span className={`ml-2 text-[13px] font-normal ${s.done ? "text-(--muted)" : ""}`}>
                  {s.done ? "(виконано)" : "(не виконано)"}
                </span>
              </span>
              {s.done ? null : (
                <Link href={s.href} className="inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline">
                  {s.action}
                </Link>
              )}
            </li>
          ))}
        </ol>
    </Card>
  );
}
