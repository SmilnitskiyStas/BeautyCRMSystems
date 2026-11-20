import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Category } from "../inventory/types";
import { CategoryPicker } from "./category-picker";

const c = (id: string, parentId: string | null, name: string, o: Partial<Category> = {}): Category => ({ id, parentId, name, sortOrder: 0, isArchived: false, productCount: 0, childCount: 0, ...o });
const CATS: Category[] = [
  c("h", null, "Волосся"),
  c("dye", "h", "Фарби"),
  c("amm", "dye", "Безаміачні"),
  c("sh", "h", "Шампуні", { sortOrder: 1 }),
  c("n", null, "Нігті", { sortOrder: 1 }),
  c("old", "n", "Старе", { isArchived: true }),
];

afterEach(() => cleanup());

function Host({ initial = null, onPick }: { initial?: string | null; onPick?: (id: string | null) => void }) {
  const [v, setV] = useState<string | null>(initial);
  return (
    <>
      <button type="button">До</button>
      <CategoryPicker
        label="Категорія"
        categories={CATS}
        value={v}
        onChange={(id) => {
          setV(id);
          onPick?.(id);
        }}
      />
      <button type="button">Після</button>
    </>
  );
}
const field = () => screen.getByRole("button", { name: /^Категорія/ });

describe("CategoryPicker", () => {
  it("поле показує шлях; панель відкривається всередині relative-контейнера поля, під кнопкою", async () => {
    const user = userEvent.setup();
    render(<Host initial="amm" />);
    expect(field()).toHaveTextContent("Волосся › Фарби › Безаміачні");
    expect(field()).toHaveAttribute("aria-expanded", "false");
    await user.click(field());
    expect(field()).toHaveAttribute("aria-expanded", "true");
    const panel = screen.getByTestId("category-panel");
    const box = screen.getByTestId("category-field");
    expect(box.className).toContain("relative");
    expect(box).toContainElement(panel);
    expect(box).toContainElement(field());
    expect(panel.className).toContain("absolute");
    expect(panel.className).toContain("top-full");
    expect(panel.className).toMatch(/z-\d+/);
    // Предки обраної категорії розгорнуті, фокус на обраному вузлі.
    const tree = within(panel).getByRole("tree", { name: "Категорія" });
    const sel = within(tree).getByRole("treeitem", { name: /Безаміачні/ });
    expect(sel).toHaveAttribute("aria-selected", "true");
    expect(sel).toHaveAttribute("aria-level", "3");
    expect(document.activeElement).toBe(sel);
  });

  it("закривається Esc (фокус повертається на поле), кліком поза і вибором", async () => {
    const user = userEvent.setup();
    const pick = vi.fn();
    render(<Host onPick={pick} />);
    await user.click(field());
    expect(screen.getByRole("tree")).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("tree")).not.toBeInTheDocument();
    expect(document.activeElement).toBe(field());
    await user.click(field());
    await user.click(screen.getByRole("button", { name: "До" }));
    expect(screen.queryByRole("tree")).not.toBeInTheDocument();
    await user.click(field());
    await user.click(screen.getByRole("treeitem", { name: "Волосся" }));
    expect(pick).toHaveBeenCalledWith("h");
    expect(screen.queryByRole("tree")).not.toBeInTheDocument();
    expect(field()).toHaveTextContent("Волосся");
  });

  it("дерево: aria-expanded на вузлах з дітьми, розгортання кліком по шеврону; архівні не пропонуються", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(field());
    const hair = screen.getByRole("treeitem", { name: "Волосся" });
    expect(hair).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("treeitem", { name: "Фарби" })).not.toBeInTheDocument();
    await user.click(screen.getByTestId("category-toggle-h"));
    expect(hair).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("treeitem", { name: "Фарби" })).toBeInTheDocument();
    // Нігті мали б єдину дитину - архівну: вузол стає листом без aria-expanded.
    expect(screen.getByRole("treeitem", { name: "Нігті" })).not.toHaveAttribute("aria-expanded");
    expect(screen.queryByRole("treeitem", { name: "Старе" })).not.toBeInTheDocument();
  });

  it("пошук фільтрує за назвою (регістронезалежно), розгортає предків; порожній результат", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(field());
    await user.type(screen.getByRole("searchbox", { name: "Пошук категорії" }), "безам");
    expect(screen.getAllByRole("treeitem").map((e) => e.textContent?.replace(/[▾▸✓]/g, ""))).toEqual(["Волосся", "Фарби", "Безаміачні"]);
    expect(screen.queryByRole("treeitem", { name: "Без категорії" })).not.toBeInTheDocument();
    await user.clear(screen.getByRole("searchbox"));
    await user.type(screen.getByRole("searchbox"), "zzz");
    expect(screen.queryAllByRole("treeitem")).toHaveLength(0);
    expect(screen.getByText("Нічого не знайдено.")).toBeInTheDocument();
  });

  it("клавіатура: стрілки, → розгорнути / в дитину, ← згорнути / до батька, Home/End, Enter вибирає", async () => {
    const user = userEvent.setup();
    const pick = vi.fn();
    render(<Host onPick={pick} />);
    await user.click(field());
    // Фокус на «Без категорії».
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Без категорії" }));
    await user.keyboard("{ArrowDown}");
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Волосся" }));
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("treeitem", { name: "Волосся" })).toHaveAttribute("aria-expanded", "true");
    await user.keyboard("{ArrowRight}");
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Фарби" }));
    await user.keyboard("{ArrowRight}{ArrowRight}");
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Безаміачні" }));
    await user.keyboard("{ArrowLeft}");
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Фарби" }));
    await user.keyboard("{ArrowLeft}");
    expect(screen.getByRole("treeitem", { name: "Фарби" })).toHaveAttribute("aria-expanded", "false");
    await user.keyboard("{End}");
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Нігті" }));
    await user.keyboard("{Home}{ArrowUp}");
    expect(document.activeElement).toBe(screen.getByRole("searchbox"));
    await user.keyboard("{ArrowDown}");
    expect(document.activeElement).toBe(screen.getByRole("treeitem", { name: "Без категорії" }));
    await user.keyboard("{End}{Enter}");
    expect(pick).toHaveBeenLastCalledWith("n");
    expect(field()).toHaveTextContent("Нігті");
    // Space вибирає, «Без категорії» очищає.
    await user.click(field());
    await user.keyboard(" ");
    expect(pick).toHaveBeenLastCalledWith("n");
    await user.click(field());
    await user.click(screen.getByRole("treeitem", { name: "Без категорії" }));
    expect(pick).toHaveBeenLastCalledWith(null);
  });

  it("Tab закриває панель без пастки фокуса", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(field());
    await user.tab();
    expect(screen.queryByRole("tree")).not.toBeInTheDocument();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Після" }));
  });

  it("архівна обрана категорія показується зі шляхом і позначкою «архів»; disabled не відкривається", async () => {
    const user = userEvent.setup();
    render(<CategoryPicker label="Категорія" categories={CATS} value="old" onChange={() => {}} />);
    expect(field()).toHaveTextContent("Нігті › Старе (архів)");
    await user.click(field());
    expect(screen.getAllByRole("treeitem").every((e) => e.getAttribute("aria-selected") === "false")).toBe(true);
    cleanup();
    render(<CategoryPicker label="Категорія" categories={CATS} value={null} onChange={() => {}} disabled />);
    expect(field()).toBeDisabled();
  });
});
