import { DEFAULT_LIMITS, cartCount, cartTotal, useCart } from "../cart";

const p = (id: string, price = 100, inStock = true) => ({ id, name: `P${id}`, unit: "pcs", price, categoryPath: null, inStock });
const c = () => useCart.getState();
beforeEach(() => useCart.setState({ locationId: null, lines: [] }));

describe("cart limits (§33.3, §33.10 B)", () => {
  it("defaults are 20 lines / 10 per line", () => {
    expect(DEFAULT_LIMITS).toEqual({ maxLines: 20, maxQuantityPerLine: 10 });
  });

  it("21st distinct product is refused with default limits; existing line can still grow", () => {
    for (let i = 0; i < 20; i++) expect(c().add(p(`p${i}`))).toBe(true);
    expect(c().add(p("p20"))).toBe(false);
    expect(c().lines).toHaveLength(20);
    expect(c().add(p("p0"))).toBe(true);
    expect(c().lines[0].quantity).toBe(2);
  });

  it("11th unit of the same product is capped at 10 (add)", () => {
    for (let i = 0; i < 15; i++) c().add(p("a"));
    expect(c().lines[0].quantity).toBe(10);
  });

  it("setQuantity clamps to 10, floors fractions, removes at <1", () => {
    c().add(p("a"));
    c().setQuantity("a", 99);
    expect(c().lines[0].quantity).toBe(10);
    c().setQuantity("a", 2.9);
    expect(c().lines[0].quantity).toBe(2);
    c().setQuantity("a", 0.5);
    expect(c().lines).toHaveLength(0);
  });

  it("server-provided limits (P1) override defaults in setQuantity and replace", () => {
    c().add(p("a"));
    c().setQuantity("a", 8, { maxLines: 5, maxQuantityPerLine: 4 });
    expect(c().lines[0].quantity).toBe(4);
    const many = Array.from({ length: 8 }, (_, i) => ({ productId: `p${i}`, name: "n", unit: "pcs", price: 1, quantity: 9 }));
    c().replace("loc1", many, { maxLines: 5, maxQuantityPerLine: 4 });
    expect(c().lines).toHaveLength(5);
    expect(c().lines.every((l) => l.quantity === 4 && !l.unavailable)).toBe(true);
    expect(c().locationId).toBe("loc1");
  });

  it("add refreshes the line price from the latest P1 item", () => {
    c().add(p("a", 100));
    c().add(p("a", 120));
    expect(c().lines[0]).toMatchObject({ price: 120, quantity: 2 });
  });
});

describe("cart location binding (§33.10 B)", () => {
  it("switching location clears lines and rebinds", () => {
    c().bind("loc1");
    c().add(p("a"));
    c().bind("loc2");
    expect(c().lines).toEqual([]);
    expect(c().locationId).toBe("loc2");
  });

  it("re-binding the same location keeps lines", () => {
    c().bind("loc1");
    c().add(p("a"));
    c().bind("loc1");
    expect(c().lines).toHaveLength(1);
  });

  it("replace() (reorder) for another location overwrites location and lines", () => {
    c().bind("loc1");
    c().add(p("a"));
    c().replace("loc2", [{ productId: "b", name: "B", unit: "pcs", price: 5, quantity: 1 }]);
    expect(c().locationId).toBe("loc2");
    expect(c().lines.map((l) => l.productId)).toEqual(["b"]);
  });

  it("clear() empties lines (after a successful order)", () => {
    c().bind("loc1");
    c().add(p("a"));
    c().clear();
    expect(c().lines).toEqual([]);
  });
});

describe("expectedTotal source (cartTotal)", () => {
  it("equals the sum of price*qty in cents, 2 decimals max (§33.5 expectedTotal)", () => {
    c().add(p("a", 420));
    c().setQuantity("a", 2);
    c().add(p("b", 199));
    expect(cartTotal(c().lines)).toBe(1039);
    c().add(p("c", 650.5));
    expect(cartTotal(c().lines)).toBe(1689.5);
  });

  it("classic float traps stay exact", () => {
    c().add(p("a", 0.1));
    c().add(p("b", 0.2));
    expect(cartTotal(c().lines)).toBe(0.3);
    c().add(p("c", 100.13));
    expect(cartTotal(c().lines)).toBe(100.43);
  });

  it("empty cart total is 0; cartCount sums quantities", () => {
    expect(cartTotal([])).toBe(0);
    c().add(p("a"));
    c().setQuantity("a", 3);
    c().add(p("b"));
    expect(cartCount(c().lines)).toBe(4);
  });

  it("total after price refresh uses the new price (expectedTotal matches what the user sees)", () => {
    c().add(p("a", 100));
    c().refresh([p("a", 110)]);
    expect(cartTotal(c().lines)).toBe(110);
  });
});
