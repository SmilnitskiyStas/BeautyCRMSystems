"use client";

import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { canManageTeam, canSeeAccessAudit } from "@/features/beauty-auth/permissions";
import { useRevokeTeamInvite, useTeamInvites, useTeamUsers } from "../hooks/use-team";
import { roleTitle } from "../team/logic";
import type { TeamInvite } from "../team/types";
import { AccessAudit } from "./access-audit";
import { ConfirmActionDialog } from "./access-dialogs";
import { TeamInvitePanel } from "./team-invite-panel";
import { UserAccessCard } from "./user-access-card";
import { Badge, Card, EmptyState, PageHeader, PrimaryButton, QueryState, SecondaryButton, TD, Table, Tabs } from "./ui";

type Tab = "team" | "invites" | "audit";

/** «Команда й доступи» (`team.manage = manage`, §30.8): список команди, картка прав, запрошення, журнал (лише директор). */
export function TeamScreen() {
  const { user } = useAuth();
  const allowed = canManageTeam(user);
  const audit = canSeeAccessAudit(user);
  const [tab, setTab] = useState<Tab>("team");
  const [selected, setSelected] = useState<string | null>(null);
  const [inviting, setInviting] = useState(false);
  const users = useTeamUsers(allowed);

  if (!allowed) {
    return (
      <>
        <PageHeader title="Команда й доступи" />
        <Card>
          <p role="note" className="m-0 text-sm">
            Керувати командою можуть лише користувачі з відповідним правом.
          </p>
        </Card>
      </>
    );
  }

  const tabs: { id: Tab; label: string }[] = [
    { id: "team", label: "Команда" },
    { id: "invites", label: "Запрошення" },
    ...(audit ? [{ id: "audit" as Tab, label: "Журнал змін" }] : []),
  ];
  const picked = users.data?.find((u) => u.id === selected) ?? null;

  return (
    <>
      <PageHeader
        title="Команда й доступи"
        subtitle="Хто що бачить і змінює. Права діють одразу після збереження."
        actions={inviting ? null : <PrimaryButton onClick={() => { setInviting(true); setTab("team"); }}>Запросити</PrimaryButton>}
      />
      {inviting ? <TeamInvitePanel onClose={() => setInviting(false)} /> : null}
      <Tabs tabs={tabs} value={tab} onChange={setTab} />
      {tab === "team" ? (
        <>
          <QueryState isPending={users.isPending} isError={users.isError} onRetry={() => void users.refetch()}>
            <Card>
              <Table headers={["Користувач", "Роль", "Статус", ""]} minWidth={560}>
                {(users.data ?? []).map((u) => (
                  <tr key={u.id} data-testid="team-row" className="border-t border-(--line)">
                    <td className={TD}>
                      <div className="font-semibold">{u.fullName}</div>
                      <div className="text-[13px] text-(--muted)">{u.email}</div>
                    </td>
                    <td className={TD}>{roleTitle(u.role)}</td>
                    <td className={TD}>
                      <Badge tone={u.isActive ? "ok" : "neutral"}>{u.isActive ? "Активний" : "Вимкнений"}</Badge>
                    </td>
                    <td className={TD}>
                      <SecondaryButton aria-label={`${u.manageable ? "Налаштувати доступ" : "Переглянути доступ"}: ${u.fullName}`} aria-expanded={selected === u.id} onClick={() => setSelected(selected === u.id ? null : u.id)}>
                        {u.manageable ? "Доступ" : "Переглянути"}
                      </SecondaryButton>
                    </td>
                  </tr>
                ))}
              </Table>
              {(users.data ?? []).length === 0 ? <EmptyState>Користувачів ще немає.</EmptyState> : null}
            </Card>
          </QueryState>
          {picked ? <UserAccessCard key={picked.id} user={picked} onClose={() => setSelected(null)} /> : null}
        </>
      ) : null}
      {tab === "invites" ? <InvitesList /> : null}
      {tab === "audit" && audit ? <AccessAudit /> : null}
    </>
  );
}

function InvitesList() {
  const q = useTeamInvites();
  const revoke = useRevokeTeamInvite();
  const [target, setTarget] = useState<TeamInvite | null>(null);
  const [error, setError] = useState<string | null>(null);
  const pending = (q.data ?? []).filter((i) => i.status === "pending");
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => void q.refetch()}>
      <Card>
        {pending.length === 0 ? (
          <EmptyState>Активних запрошень немає.</EmptyState>
        ) : (
          <Table headers={["Пошта", "Роль", "Діє до", ""]} minWidth={520}>
            {pending.map((i) => (
              <tr key={i.id} data-testid="invite-row" className="border-t border-(--line)">
                <td className={TD}>{i.email}</td>
                <td className={TD}>{roleTitle(i.role)}</td>
                <td className={TD}>{new Date(i.expiresAt).toLocaleDateString("uk-UA")}</td>
                <td className={TD}>
                  <SecondaryButton aria-label={`Скасувати запрошення: ${i.email}`} onClick={() => { setError(null); setTarget(i); }}>
                    Скасувати
                  </SecondaryButton>
                </td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      {target ? (
        <ConfirmActionDialog
          title="Скасувати запрошення?"
          confirmLabel="Скасувати запрошення"
          busy={revoke.isPending}
          error={error}
          onClose={() => setTarget(null)}
          onConfirm={() =>
            revoke.mutate(target.id, { onSuccess: () => setTarget(null), onError: (e) => setError(humanizeError(e, "Не вдалося скасувати запрошення.")) })
          }
        >
          <p className="m-0">Посилання для {target.email} перестане діяти.</p>
        </ConfirmActionDialog>
      ) : null}
    </QueryState>
  );
}
