import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { ModalDialog } from "./modal-dialog";

describe("ModalDialog (портал)", () => {
  it("рендериться всередині .beauty-admin, щоб діалог мав токени теми й видимий колір тексту", () => {
    render(
      <ModalDialog title="Надіслати клієнтам?" actions={<button type="button">Так</button>} onClose={vi.fn()}>
        <p>Умови знижки</p>
      </ModalDialog>,
    );
    const dialog = screen.getByRole("dialog");
    expect(dialog.closest(".beauty-admin")).not.toBeNull();
    expect(screen.getByText("Умови знижки")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Так" })).toBeTruthy();
  });

  it("повертає фокус усередину, якщо Tab натиснуто поза діалогом, і ставить inert на решту сторінки", () => {
    const { container, unmount } = render(
      <>
        <button type="button">Фон</button>
        <ModalDialog title="Заголовок" actions={<button type="button">Так</button>} onClose={vi.fn()}>
          <input aria-label="Поле" />
        </ModalDialog>
      </>,
    );
    expect(container.hasAttribute("inert")).toBe(true);
    (screen.getByText("Фон") as HTMLElement).focus();
    fireEvent.keyDown(document, { key: "Tab" });
    expect(screen.getByRole("dialog").contains(document.activeElement)).toBe(true);
    unmount();
    expect(container.hasAttribute("inert")).toBe(false);
  });

  it("циклічно тримає Tab на межах", () => {
    render(
      <ModalDialog title="Заголовок" actions={<button type="button">Так</button>} onClose={vi.fn()}>
        <input aria-label="Поле" />
      </ModalDialog>,
    );
    const last = screen.getByRole("button", { name: "Так" });
    last.focus();
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Tab" });
    expect(document.activeElement).toBe(screen.getByLabelText("Поле"));
  });
});
