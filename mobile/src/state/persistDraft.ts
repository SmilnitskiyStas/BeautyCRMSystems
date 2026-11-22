import { localStore } from "@/services";
import { useDraft } from "@/state/draft";
import { draftKey, toBookingDraft } from "@/state/draftPersistence";

/**
 * Зберігає незавершений запис у локальне сховище при зміні закладу/послуги/майстра/дати.
 * Скидання чернетки (вибір іншого закладу, reset) сховище не чистить: незавершений запис лишається доступним
 * на головній, доки його не завершено (checkout) або не прибрано вручну. Повертає функцію відписки.
 */
export function startDraftPersistence(): () => void {
  let last = "";
  return useDraft.subscribe((st) => {
    const d = toBookingDraft(st);
    const key = draftKey(d);
    if (!d) {
      last = ""; // після reset той самий вибір знову має записатися
      return;
    }
    if (key === last) return;
    last = key;
    void localStore.setBookingDraft(d).catch(() => undefined);
  });
}
