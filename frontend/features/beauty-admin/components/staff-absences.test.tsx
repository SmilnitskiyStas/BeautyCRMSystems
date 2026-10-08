import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { Absence, StaffMember } from "../types";

const getAbsences = vi.fn<() => Promise<Absence[]>>();
vi.mock("../api", () => ({ beautyApi: { getAbsences: () => getAbsences() } }));

import { AbsencesTab } from "./staff-absences";

const member: StaffMember = { id: "s1", name: "Марина", phone: null, position: null, isActive: true, services: [], locations: [] };

function renderTab(isManager: boolean, isSelf: boolean) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AbsencesTab member={member} isManager={isManager} isSelf={isSelf} formOpen={false} onFormOpenChange={() => {}} />
    </QueryClientProvider>,
  );
}

const base: Absence = { id: "a1", specialistId: "s1", type: "sick", dateFrom: "2026-10-10", dateTo: "2026-10-11", status: "approved" };

describe("видимість note відсутності", () => {
  it("показує примітку, коли backend її віддав (керівник / автор)", async () => {
    getAbsences.mockResolvedValue([{ ...base, note: "ГРВІ, лікарняний до п'ятниці" }]);
    renderTab(true, false);
    expect(await screen.findByText(/ГРВІ, лікарняний/)).toBeInTheDocument();
  });

  it("не рендерить примітку, коли поля немає у відповіді (колега)", async () => {
    getAbsences.mockResolvedValue([base]);
    renderTab(false, false);
    expect(await screen.findByText(/Лікарняний/)).toBeInTheDocument();
    expect(screen.queryByText(/Примітка/)).not.toBeInTheDocument();
  });
});
