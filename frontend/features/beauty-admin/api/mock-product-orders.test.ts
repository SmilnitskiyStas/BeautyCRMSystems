import { describe, expect, it } from "vitest";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockProductOrders, maskPhone } from "./mock-product-orders";

const NOW = Date.parse("2026-10-14T09:00:00Z");

function make(role = "owner") {
  const state = { role, now: NOW };
  const api = createMockProductOrders({ role: () => state.role, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => state.now, wait: false });
  return { api, state };
}
async function expectError(p: Promise<unknown>, status: number, code: string) {
  await expect(p).rejects.toMatchObject({ status, code });
}
const numbers = async (api: ReturnType<typeof make>["api"], q = {}) => (await api.getProductOrders({ take: 200, ...q })).items.map((o) => o.number);

describe("mock замовлень: список", () => {
  it("за замовчуванням open (оплачені + очікують оплати), новіші першими; прострочений резерв не в open", async () => {
    const { api } = make();
    expect(await numbers(api)).toEqual([1040, 1041, 1042, 1034]);
    const items = (await api.getProductOrders({})).items;
    expect(items.map((o) => o.createdAt)).toEqual([...items.map((o) => o.createdAt)].sort().reverse());
    expect(items.every((o) => o.status === "paid" || o.status === "pending_payment")).toBe(true);
  });

  it("ефективний статус: pending_payment з минулим резервом -> expired", async () => {
    const { api } = make();
    expect(await numbers(api, { status: "expired" })).toEqual([1036]);
    expect(await numbers(api, { status: "pending_payment" })).toEqual(expect.not.arrayContaining([1036]));
  });

  it("фільтри статусу, закладу, номера; all", async () => {
    const { api } = make();
    expect(await numbers(api, { status: "fulfilled" })).toEqual([1039]);
    expect(await numbers(api, { status: "refunded" })).toEqual([1038]);
    expect(await numbers(api, { status: "cancelled" })).toEqual([1037]);
    expect((await numbers(api, { status: "all" })).length).toBe(8);
    expect(await numbers(api, { status: "all", locationId: "p" })).toEqual(expect.arrayContaining([1039, 1036, 1034]));
    expect(await numbers(api, { status: "all", number: 1042 })).toEqual([1042]);
    expect(await numbers(api, { status: "all", number: 9999 })).toEqual([]);
  });

  it("період: за замовчуванням 30 діб; вужчий період відсікає старі; > 366 діб - 422", async () => {
    const { api } = make();
    const from = new Date(NOW - 24 * 3600_000).toISOString();
    expect(await numbers(api, { status: "all", from })).not.toContain(1038);
    await expectError(api.getProductOrders({ from: "2020-01-01T00:00:00Z", to: "2026-10-14T00:00:00Z" }), 422, "invalid_range");
    await expectError(api.getProductOrders({ from: "не-дата" }), 422, "invalid_range");
  });

  it("пагінація курсором", async () => {
    const { api } = make();
    const p1 = await api.getProductOrders({ status: "all", take: 3 });
    expect(p1.items).toHaveLength(3);
    expect(p1.hasMore).toBe(true);
    const p2 = await api.getProductOrders({ status: "all", take: 3, cursor: p1.nextCursor });
    expect(p2.items.map((o) => o.id)).not.toEqual(expect.arrayContaining(p1.items.map((o) => o.id)));
    await expectError(api.getProductOrders({ cursor: "x" }), 422, "invalid_cursor");
  });

  it("невідомий статус - 422, заклад поза scope - 404", async () => {
    const { api } = make("specialist");
    await expectError(api.getProductOrders({ status: "zzz" as never }), 422, "invalid_status");
    await expectError(api.getProductOrders({ locationId: "p" }), 404, "location_not_found");
  });

  it("лічильник paid у scope", async () => {
    const { api } = make();
    expect(await api.getProductOrderCounts()).toEqual({ paid: 2 });
    const s = make("specialist");
    expect(await s.api.getProductOrderCounts()).toEqual({ paid: 1 });
  });
});

describe("mock замовлень: телефон і scope", () => {
  it("owner/admin - повний телефон; specialist - маска й лише свій заклад", async () => {
    const { api } = make();
    const o = (await api.getProductOrders({ status: "all", number: 1042 })).items[0]!;
    expect(o.customer.phone).toBe("+380501234567");
    expect(o.customer.phoneMasked).toBeNull();
    const s = make("specialist");
    const m = (await s.api.getProductOrders({ status: "all", number: 1042 })).items[0]!;
    expect(m.customer.phone).toBeNull();
    expect(m.customer.phoneMasked).toBe("+••••••••••67");
    expect(await numbers(s.api, { status: "all" })).not.toContain(1039);
    await expectError(s.api.getProductOrder("po-1039"), 404, "product_order_not_found");
  });
  it("маска", () => expect(maskPhone("+380501234567")).toBe("+••••••••••67"));
  it("деталь: рядки з сумами, без закупівельних цін", async () => {
    const { api } = make();
    const o = await api.getProductOrder("po-1042");
    expect(o.lines).toHaveLength(2);
    expect(o.total).toBe(690);
    expect(o.lines.map((l) => l.lineTotal)).toEqual([450, 240]);
    expect(JSON.stringify(o)).not.toMatch(/purchase/i);
  });
});

describe("mock замовлень: дії", () => {
  it("specialist: перегляд є, дії - 403 forbidden_role", async () => {
    const { api } = make("specialist");
    await expectError(api.fulfillProductOrder("po-1042"), 403, "forbidden_role");
    await expectError(api.cancelProductOrder("po-1042"), 403, "forbidden_role");
    await expectError(api.reconcileProductOrderPayment("po-1041"), 403, "forbidden_role");
  });

  it("fulfill: paid -> fulfilled; повтор - noop; не paid - 409 order_not_paid", async () => {
    const { api } = make();
    const o = await api.fulfillProductOrder("po-1042");
    expect(o.status).toBe("fulfilled");
    expect(o.fulfilledBy?.name).toBeTruthy();
    expect(o.saleOperationId).toBeTruthy();
    const again = await api.fulfillProductOrder("po-1042");
    expect(again.fulfilledAt).toBe(o.fulfilledAt);
    await expectError(api.fulfillProductOrder("po-1040"), 409, "order_not_paid");
    await expectError(api.fulfillProductOrder("po-1037"), 409, "order_not_paid");
    await expectError(api.fulfillProductOrder("po-нема"), 404, "product_order_not_found");
    expect(await api.getProductOrderCounts()).toEqual({ paid: 1 });
  });

  it("cancel неоплаченого -> cancelled, refundAmount 0, причина зберігається", async () => {
    const { api } = make();
    const r = await api.cancelProductOrder("po-1040", "  Дубль  ");
    expect(r.refundAmount).toBe(0);
    expect(r.order.status).toBe("cancelled");
    expect(r.order.closeReason).toBe("staff");
    expect(r.order.cancelNote).toBe("Дубль");
    await expectError(api.cancelProductOrder("po-1040"), 409, "order_closed");
  });

  it("cancel оплаченого: перша спроба на №1034 - 402 refund_failed (лишається paid), друга - refunded на повну суму", async () => {
    const { api } = make();
    await expectError(api.cancelProductOrder("po-1034"), 402, "refund_failed");
    expect((await api.getProductOrder("po-1034")).status).toBe("paid");
    const r = await api.cancelProductOrder("po-1034");
    expect(r.order.status).toBe("refunded");
    expect(r.refundAmount).toBe(830);
    expect(r.order.returnOperationId).toBeTruthy();
    expect(r.order.payment?.status).toBe("refunded");
  });

  it("cancel: виданого - order_fulfilled, закритого - order_closed, причина > 300 - 422", async () => {
    const { api } = make();
    await expectError(api.cancelProductOrder("po-1039"), 409, "order_fulfilled");
    await expectError(api.cancelProductOrder("po-1038"), 409, "order_closed");
    await expectError(api.cancelProductOrder("po-1036"), 409, "order_closed");
    await expectError(api.cancelProductOrder("po-1042", "я".repeat(301)), 422, "invalid_reason");
    expect((await api.cancelProductOrder("po-1042", "я".repeat(300))).order.status).toBe("refunded");
  });

  it("оплата в процесі: cancel - payment_in_progress; reconcile -> paid; повтор - nothing_to_reconcile", async () => {
    const { api } = make();
    await expectError(api.cancelProductOrder("po-1041"), 409, "payment_in_progress");
    const o = await api.reconcileProductOrderPayment("po-1041");
    expect(o.status).toBe("paid");
    expect(o.paymentInProgress).toBe(false);
    expect(o.payment?.status).toBe("paid");
    await expectError(api.reconcileProductOrderPayment("po-1041"), 409, "nothing_to_reconcile");
    expect(await api.getProductOrderCounts()).toEqual({ paid: 3 });
  });
});
