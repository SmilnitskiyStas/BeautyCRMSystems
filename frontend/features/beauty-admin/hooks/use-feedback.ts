"use client";

import { useMutation } from "@tanstack/react-query";
import { beautyApi } from "../api";
import type { FeedbackInput } from "../types";

/** Відправка звернення до розробників (§27.9). Кешу немає: відповідь лише `{id}`. */
export const useSendFeedback = () => useMutation({ mutationFn: (input: FeedbackInput) => beautyApi.sendFeedback(input) });
