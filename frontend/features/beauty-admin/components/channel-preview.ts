import type { ChannelConfig, PreviewMessage } from "../types";

/** Чиста функція попереднього перегляду розмови за налаштуваннями каналу. */
export function buildChannelPreview(c: ChannelConfig): PreviewMessage[] {
  if (!c.connected) {
    return [{ from: "bot", text: "Підключіть канал, щоб побачити, як виглядатиме розмова." }];
  }
  if (!c.ai) {
    return [
      { from: "client", text: "Добрий день! Чи є вільний час на манікюр сьогодні?" },
      { from: "manager", text: "Повідомлення отримано. Менеджер відповість у робочі години." },
    ];
  }
  if (c.booking) {
    return [
      { from: "bot", text: c.greeting },
      { from: "client", text: "Хочу манікюр з гель-лаком сьогодні ввечері" },
      { from: "bot", text: "Є вільні вікна о 17:00 та 18:30 у закладі Центр, майстер Марина. Коштує 900 ₴, триває 1 г 30 хв. Записати на 17:00?" },
      { from: "client", text: "Так, на 17:00" },
      { from: "bot", text: "Готово! Запис на 17:00 створено. Нагадаю за 1 годину." },
    ];
  }
  return [
    { from: "bot", text: c.greeting },
    { from: "client", text: "Скільки коштує манікюр з гель-лаком?" },
    { from: "bot", text: "900 ₴, триває 1 г 30 хв. Записатися можна на нашому сайті, або напишіть, і менеджер допоможе." },
  ];
}

export const AI_MODES: { id: ChannelConfig["mode"]; label: string; hint: string }[] = [
  { id: "suggest", label: "Тільки підказує", hint: "AI готує чернетку, а надсилає її людина." },
  { id: "confirm", label: "Готує, я підтверджую", hint: "AI пише відповідь і створює запис, але чекає вашого підтвердження." },
  { id: "auto", label: "Відповідає сам", hint: "AI самостійно відповідає й записує в межах ваших правил, складні випадки передає менеджеру." },
];
