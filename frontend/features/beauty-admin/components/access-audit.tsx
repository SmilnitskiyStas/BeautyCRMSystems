"use client";

import { humanizeError } from "@/features/beauty-auth/errors";
import { useAccessAudit } from "../hooks/use-team";
import { AUDIT_ACTION } from "../team/logic";
import { ChangeList } from "./access-dialogs";
import { Card, EmptyState, QueryState, SecondaryButton } from "./ui";

/** Журнал змін доступів (лише директор, §30.9.6): без ПІІ клієнтів; сторінки за курсором. */
export function AccessAudit({ userId = null }: { userId?: string | null }) {
  const q = useAccessAudit(userId);
  const items = q.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => void q.refetch()}>
      <Card className="flex flex-col gap-3">
        <h2 className="m-0 text-lg font-semibold">Журнал змін</h2>
        {items.length === 0 ? <EmptyState>Записів ще немає.</EmptyState> : null}
        <ol className="m-0 flex list-none flex-col gap-3 p-0" aria-label="Журнал змін доступів">
          {items.map((i) => (
            <li key={i.id} className="flex flex-col gap-1.5 border-t border-(--line) pt-3 first:border-t-0 first:pt-0" data-testid="audit-item">
              <div className="text-sm">
                <strong>{AUDIT_ACTION[i.action] ?? i.action}</strong>
                {i.target ? <> · {i.target.fullName}</> : null}
              </div>
              <div className="text-[13px] text-(--muted)">
                <time dateTime={i.createdAt}>{new Date(i.createdAt).toLocaleString("uk-UA")}</time> · {i.actor ? i.actor.fullName : "Система"}
              </div>
              {i.changes.length > 0 ? <ChangeList changes={i.changes} /> : null}
            </li>
          ))}
        </ol>
        {q.isFetchNextPageError ? <p role="alert" className="m-0 text-sm text-[#8A1F1F]">{humanizeError(q.error, "Не вдалося завантажити наступну сторінку.")}</p> : null}
        {q.hasNextPage ? (
          <SecondaryButton onClick={() => void q.fetchNextPage()} disabled={q.isFetchingNextPage}>
            {q.isFetchingNextPage ? "Завантажуємо…" : "Показати ще"}
          </SecondaryButton>
        ) : null}
      </Card>
    </QueryState>
  );
}
