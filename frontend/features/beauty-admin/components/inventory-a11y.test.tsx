import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { InputField, LoadMore, quantityInputMode, useFocusInvalid } from "./inventory-parts";
import { Table } from "./ui";

vi.mock("next/navigation", () => ({ usePathname: () => "/beauty/inventory" }));

function Form() {
  const [attachInvalid, requestInvalid] = useFocusInvalid<HTMLFormElement>();
  const [err, setErr] = useState(false);
  return (
    <form
      ref={attachInvalid}
      onSubmit={(e) => {
        e.preventDefault();
        setErr(true);
        requestInvalid();
      }}
    >
      <InputField label="Назва" value="" onChange={() => {}} />
      <InputField label="Код" value="" onChange={() => {}} error={err ? "Помилка" : undefined} />
      <button type="submit">Зберегти</button>
    </form>
  );
}

describe("a11y складу", () => {
  it("після невдалого сабміту фокус на першому полі з aria-invalid", async () => {
    render(<Form />);
    await userEvent.click(screen.getByRole("button", { name: "Зберегти" }));
    expect(document.activeElement).toBe(screen.getByLabelText("Код"));
  });

  it("Table: caption і прокручувана область з клавіатурним фокусом", () => {
    render(
      <Table caption="Залишки" headers={["A"]}>
        <tr>
          <td>1</td>
        </tr>
      </Table>,
    );
    expect(screen.getByRole("table", { name: "Залишки" })).toBeTruthy();
    const region = screen.getByRole("region", { name: "Залишки" });
    expect(region.getAttribute("tabindex")).toBe("0");
  });

  it("inputMode: numeric для цілих одиниць, decimal для дробових; tel для телефону", () => {
    expect(quantityInputMode("pcs")).toBe("numeric");
    expect(quantityInputMode("pack")).toBe("numeric");
    expect(quantityInputMode("ml")).toBe("decimal");
    render(<InputField label="Телефон" type="tel" inputMode="tel" value="" onChange={() => {}} />);
    const input = screen.getByLabelText("Телефон");
    expect(input.getAttribute("type")).toBe("tel");
    expect(input.getAttribute("autocomplete")).toBe("tel");
  });

  it("LoadMore: коли кнопка зникає, фокус переходить на статус", async () => {
    function Host() {
      const [more, setMore] = useState(true);
      return <LoadMore hasMore={more} loading={false} shown={5} onClick={() => setMore(false)} />;
    }
    render(<Host />);
    await userEvent.click(screen.getByRole("button", { name: "Показати ще" }));
    expect(document.activeElement).toBe(screen.getByRole("status"));
  });
});
