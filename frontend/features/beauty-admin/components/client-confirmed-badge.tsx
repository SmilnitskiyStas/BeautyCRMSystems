import { dateTimeLabel } from "../format";
import { Badge } from "./ui";

/** «Клієнт підтвердив візит {час}» (§23.10): момент підтвердження кнопкою в чаті, у зоні закладу. */
export function ConfirmedBadge({ at, timezone }: { at: string; timezone?: string }) {
  return (
    <Badge tone="ok" className="self-start" >
      <span data-testid="client-confirmed">
        <span aria-hidden="true">✓ </span>
        Клієнт підтвердив візит {dateTimeLabel(at, timezone)}
      </span>
    </Badge>
  );
}
