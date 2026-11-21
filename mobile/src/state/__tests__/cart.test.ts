import { cartTotal, useCart } from "../cart";

const p = (id: string, price = 100, inStock = true) => ({ id, name: `P${id}`, unit: "pcs", price, categoryPath: null, inStock });

beforeEach(() => useCart.setState({ locationId: null, lines: [] }));

describe("cart", () => {
  it("adds, increments up to the per-line limit, ignores out-of-stock", () => {
    const c = () => useCart.getState();
    const lim = { maxLines: 20, maxQuantityPerLine: 3 };
    expect(c().add(p("a"), lim)).toBe(true);
    for (let i = 0; i < 5; i++) c().add(p("a"), lim);
    expect(c().lines[0].quantity).toBe(3);
    expect(c().add(p("b", 1, false), lim)).toBe(false);
    expect(c().lines).toHaveLength(1);
  });

  it("refuses a new line beyond maxLines", () => {
    const lim = { maxLines: 2, maxQuantityPerLine: 10 };
    useCart.getState().add(p("a"), lim);
    useCart.getState().add(p("b"), lim);
    expect(useCart.getState().add(p("c"), lim)).toBe(false);
    expect(useCart.getState().add(p("a"), lim)).toBe(true);
  });

  it("changing location clears the cart; same location keeps it", () => {
    useCart.getState().bind("loc1");
    useCart.getState().add(p("a"));
    useCart.getState().bind("loc1");
    expect(useCart.getState().lines).toHaveLength(1);
    useCart.getState().bind("loc2");
    expect(useCart.getState().lines).toHaveLength(0);
  });

  it("total is computed in cents (no float drift)", () => {
    useCart.getState().add(p("a", 0.1));
    useCart.getState().setQuantity("a", 3);
    expect(cartTotal(useCart.getState().lines)).toBe(0.3);
  });

  it("refresh updates prices and flags missing/out-of-stock; quantity change clears the flag", () => {
    useCart.getState().add(p("a", 100));
    useCart.getState().add(p("b", 50));
    useCart.getState().refresh([p("a", 120, false)]);
    const [a, b] = useCart.getState().lines;
    expect(a).toMatchObject({ price: 120, unavailable: true });
    expect(b.unavailable).toBe(true);
    useCart.getState().setQuantity("a", 2);
    expect(useCart.getState().lines[0].unavailable).toBe(false);
  });

  it("setQuantity 0 removes the line; markUnavailable / removeMany work by id", () => {
    useCart.getState().add(p("a"));
    useCart.getState().add(p("b"));
    useCart.getState().markUnavailable(["a"]);
    expect(useCart.getState().lines[0].unavailable).toBe(true);
    useCart.getState().setQuantity("b", 0);
    useCart.getState().removeMany(["a"]);
    expect(useCart.getState().lines).toHaveLength(0);
  });
});
