"use client";

import { accessOf } from "@/features/beauty-auth/access";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canManageTelegramLink } from "../inventory/telegram-link";
import { TelegramLinkCard } from "./telegram-link-card";
import { Card, EmptyState, PageHeader, TextLink } from "./ui";

/** `/beauty/settings/notifications` (§32.24.9): сповіщення складу в Telegram. Права дзеркалять backend: лише owner і модуль `beauty_inventory`. */
export function TelegramNotificationsScreen() {
  const allowed = canManageTelegramLink(accessOf(useAuth().user));
  return (
    <>
      <PageHeader title="Сповіщення" subtitle="Службові сповіщення складу в Telegram для власника" />
      <div className="grid grid-cols-1 items-start gap-6">
        {allowed ? (
          <TelegramLinkCard />
        ) : (
          <Card>
            <EmptyState>Сповіщення складу в Telegram доступні лише власнику, коли підключено модуль «Склад».</EmptyState>
          </Card>
        )}
        <TextLink href="/beauty/settings">До налаштувань</TextLink>
      </div>
    </>
  );
}

/** Посилання на екран сповіщень (налаштування, алерти складу): лише owner з модулем `beauty_inventory`. */
export function TelegramSettingsLink() {
  if (!canManageTelegramLink(accessOf(useAuth().user))) return null;
  return <TextLink href="/beauty/settings/notifications">Сповіщення складу в Telegram</TextLink>;
}
