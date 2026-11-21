import { Pressable, Text } from "react-native";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

/** Кнопка кількості/«Додати» (≥ 44px, з a11y-підписом). */
export function Stepper({ label, a11y, onPress, disabled, wide }: { label: string; a11y: string; onPress: () => void; disabled?: boolean; wide?: boolean }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={a11y}
      accessibilityState={{ disabled: !!disabled }}
      disabled={disabled}
      onPress={onPress}
      style={{ minHeight: TOUCH, minWidth: TOUCH, paddingHorizontal: wide ? space.md : 0, borderRadius: radius.md, borderWidth: 1, borderColor: colors.primary, alignItems: "center", justifyContent: "center", opacity: disabled ? 0.4 : 1 }}
    >
      <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.primary }}>{label}</Text>
    </Pressable>
  );
}
