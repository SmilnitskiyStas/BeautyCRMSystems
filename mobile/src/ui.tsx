import type { ReactNode } from "react";
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, View, type TextInputProps } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

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

export function Field(props: TextInputProps & { label: string; error?: string }) {
  const { label, error, ...rest } = props;
  return (
    <View style={{ gap: space.xs }}>
      <Label>{label}</Label>
      <TextInput
        accessibilityLabel={label}
        placeholderTextColor={colors.muted}
        {...rest}
        style={[s.input, error ? { borderColor: colors.danger } : null]}
      />
      {error ? <Text style={s.error}>{error}</Text> : null}
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
  error: { fontFamily: fonts.body, fontSize: 13, color: colors.danger },
});
