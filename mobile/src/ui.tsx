import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, View, type TextInputProps } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { colors, fonts, radius, space, TOUCH } from "@/theme";
import { InputSheet } from "@/components/InputSheet";

export function Screen({ children, scroll = true }: { children: ReactNode; scroll?: boolean }) {
  return (
    <SafeAreaView style={s.screen} edges={["bottom", "left", "right"]}>
      {scroll ? (
        <ScrollView contentContainerStyle={s.content} keyboardShouldPersistTaps="handled">
          {children}
        </ScrollView>
      ) : (
        <View style={s.content}>{children}</View>
      )}
    </SafeAreaView>
  );
}

export const H1 = ({ children }: { children: ReactNode }) => (
  <Text accessibilityRole="header" style={s.h1}>
    {children}
  </Text>
);
export const Label = ({ children }: { children: ReactNode }) => <Text style={s.label}>{children}</Text>;
export const Body = ({ children, muted }: { children: ReactNode; muted?: boolean }) => (
  <Text style={[s.body, muted && { color: colors.muted }]}>{children}</Text>
);

export function Button({
  title,
  onPress,
  variant = "primary",
  disabled,
  loading,
}: {
  title: string;
  onPress: () => void;
  variant?: "primary" | "secondary" | "danger";
  disabled?: boolean;
  loading?: boolean;
}) {
  const off = disabled || loading;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled: !!off, busy: !!loading }}
      disabled={off}
      onPress={onPress}
      style={[s.btn, variant === "primary" && s.btnPrimary, variant === "secondary" && s.btnSecondary, variant === "danger" && s.btnDanger, off && { opacity: 0.5 }]}
    >
      {loading ? (
        <ActivityIndicator color={variant === "primary" ? colors.onPrimary : colors.primary} />
      ) : (
        <Text style={[s.btnText, variant === "primary" && { color: colors.onPrimary }, variant === "secondary" && { color: colors.primary }, variant === "danger" && { color: colors.danger }]}>
          {title}
        </Text>
      )}
    </Pressable>
  );
}

/** Рядок-картка, що натискається (≥ 44px). */
export function Row({ title, subtitle, right, onPress }: { title: string; subtitle?: string; right?: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={s.row}>
      <View style={{ flex: 1 }}>
        <Text style={s.rowTitle}>{title}</Text>
        {subtitle ? <Text style={s.rowSub}>{subtitle}</Text> : null}
      </View>
      {right ? <Text style={s.rowRight}>{right}</Text> : null}
    </Pressable>
  );
}

export function Chip({ label, selected, disabled, onPress }: { label: string; selected?: boolean; disabled?: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: !!selected, disabled: !!disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[s.chip, selected && s.chipOn, disabled && { opacity: 0.4 }]}
    >
      <Text style={[s.chipText, selected && { color: colors.onPrimary }]}>{label}</Text>
    </Pressable>
  );
}

type FieldProps = Omit<TextInputProps, "onSubmitEditing"> & {
  label: string;
  error?: string;
  /** Викликається після «Enter» у панелі вводу, коли нове значення вже передане в onChangeText. */
  onSubmitEditing?: () => void;
};

/**
 * Поле форми. Сам TextInput на екрані не відкривається: тап відкриває панель над клавіатурою ({@link InputSheet}),
 * а «Готово» переносить текст назад у поле. Так клавіатура не перекриває поле на жодному екрані.
 */
export function Field(props: FieldProps) {
  const { label, error, value, onChangeText, onSubmitEditing, placeholder, secureTextEntry, editable = true, ...rest } = props;
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [pendingSubmit, setPendingSubmit] = useState(false);
  // onSubmitEditing екрана читає свій state: викликаємо його після ре-рендеру з новим значенням, а не зі старим замиканням
  const submitRef = useRef(onSubmitEditing);
  submitRef.current = onSubmitEditing;
  useEffect(() => {
    if (!pendingSubmit) return;
    setPendingSubmit(false);
    submitRef.current?.();
  }, [pendingSubmit]);

  const text = value ?? "";
  const shown = text ? (secureTextEntry ? "•".repeat(text.length) : text) : (placeholder ?? "");
  return (
    <View style={{ gap: space.xs }}>
      <Label>{label}</Label>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={label}
        accessibilityValue={{ text: secureTextEntry ? undefined : text }}
        accessibilityState={{ disabled: !editable, expanded: open }}
        disabled={!editable}
        onPress={() => setOpen(true)}
        style={[s.input, s.inputBox, error ? { borderColor: colors.danger } : null, !editable && { opacity: 0.5 }]}
      >
        <Text numberOfLines={rest.multiline ? undefined : 1} style={[s.inputText, !text && { color: colors.muted }]}>
          {shown}
        </Text>
      </Pressable>
      {error ? <Text style={s.error}>{error}</Text> : null}
      <InputSheet
        visible={open}
        label={label}
        initialValue={text}
        inputProps={{ ...rest, placeholder, secureTextEntry }}
        doneLabel={t("common.done")}
        cancelLabel={t("common.cancel")}
        onCancel={() => setOpen(false)}
        onDone={(v, submitted) => {
          setOpen(false);
          onChangeText?.(v);
          if (submitted && onSubmitEditing) setPendingSubmit(true);
        }}
      />
    </View>
  );
}

export const ErrorText = ({ children }: { children: ReactNode }) => (
  <Text accessibilityRole="alert" style={s.error}>
    {children}
  </Text>
);

export const Spinner = () => <ActivityIndicator color={colors.primary} style={{ marginVertical: space.xl }} />;

const s = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.bg },
  content: { padding: space.lg, gap: space.md },
  h1: { fontFamily: fonts.heading, fontSize: 22, color: colors.text },
  label: { fontFamily: fonts.bodyMedium, fontSize: 14, color: colors.text },
  body: { fontFamily: fonts.body, fontSize: 15, color: colors.text },
  btn: { minHeight: TOUCH, borderRadius: radius.md, alignItems: "center", justifyContent: "center", paddingHorizontal: space.lg },
  btnPrimary: { backgroundColor: colors.primary },
  btnSecondary: { borderWidth: 1, borderColor: colors.primary, backgroundColor: colors.surface },
  btnDanger: { borderWidth: 1, borderColor: colors.danger, backgroundColor: colors.surface },
  btnText: { fontFamily: fonts.bodyMedium, fontSize: 16 },
  row: { minHeight: TOUCH, flexDirection: "row", alignItems: "center", gap: space.md, backgroundColor: colors.surface, borderRadius: radius.md, padding: space.md, borderWidth: 1, borderColor: colors.border },
  rowTitle: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text },
  rowSub: { fontFamily: fonts.body, fontSize: 13, color: colors.muted, marginTop: 2 },
  rowRight: { fontFamily: fonts.bodyMedium, fontSize: 15, color: colors.primary },
  chip: { minHeight: TOUCH, minWidth: TOUCH, paddingHorizontal: space.lg, borderRadius: radius.md, borderWidth: 1, borderColor: colors.border, backgroundColor: colors.surface, alignItems: "center", justifyContent: "center" },
  chipOn: { backgroundColor: colors.primary, borderColor: colors.primary },
  chipText: { fontFamily: fonts.bodyMedium, fontSize: 15, color: colors.text },
  input: { minHeight: TOUCH, borderWidth: 1, borderColor: colors.border, borderRadius: radius.md, backgroundColor: colors.surface, paddingHorizontal: space.md, fontFamily: fonts.body, fontSize: 16, color: colors.text },
  inputBox: { justifyContent: "center", paddingVertical: space.sm },
  inputText: { fontFamily: fonts.body, fontSize: 16, color: colors.text },
  error: { fontFamily: fonts.body, fontSize: 13, color: colors.danger },
});
