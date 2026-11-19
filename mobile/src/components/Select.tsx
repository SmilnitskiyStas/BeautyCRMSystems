import { useState } from "react";
import { Modal, Pressable, StyleSheet, Text, View } from "react-native";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

export interface SelectOption<T extends string> {
  value: T;
  label: string;
}

/** Випадаючий список: закрите поле (підпис + значення + шеврон) і bottom sheet у вбудованому Modal. Android back закриває (onRequestClose). */
export function Select<T extends string>({
  label,
  value,
  options,
  onChange,
  closeLabel,
}: {
  label: string;
  value: T;
  options: readonly SelectOption<T>[];
  onChange: (v: T) => void;
  closeLabel: string;
}) {
  const [open, setOpen] = useState(false);
  const current = options.find((o) => o.value === value)?.label ?? "";
  return (
    <View style={{ gap: space.xs }}>
      <Text style={s.label}>{label}</Text>
      <Pressable
        accessibilityRole="combobox"
        accessibilityLabel={label}
        accessibilityValue={{ text: current }}
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen(true)}
        style={s.field}
      >
        <Text style={s.value}>{current}</Text>
        <Text accessible={false} style={s.chevron}>
          {open ? "▴" : "▾"}
        </Text>
      </Pressable>
      <Modal visible={open} transparent animationType="fade" onRequestClose={() => setOpen(false)} statusBarTranslucent>
        <View style={s.overlay}>
          <Pressable accessibilityLabel={closeLabel} accessibilityRole="button" style={StyleSheet.absoluteFill} onPress={() => setOpen(false)} />
          <View style={s.sheet} accessibilityViewIsModal>
            <Text accessibilityRole="header" style={s.sheetTitle}>
              {label}
            </Text>
            {options.map((o) => {
              const on = o.value === value;
              return (
                <Pressable
                  key={o.value}
                  accessibilityRole="radio"
                  accessibilityLabel={o.label}
                  accessibilityState={{ selected: on, checked: on }}
                  onPress={() => {
                    onChange(o.value);
                    setOpen(false);
                  }}
                  style={[s.option, on && s.optionOn]}
                >
                  <Text style={[s.optionText, on && { fontFamily: fonts.bodyMedium, color: colors.primary }]}>{o.label}</Text>
                  {on ? (
                    <Text accessible={false} style={s.check}>
                      ✓
                    </Text>
                  ) : null}
                </Pressable>
              );
            })}
            <Pressable accessibilityRole="button" onPress={() => setOpen(false)} style={s.close}>
              <Text style={s.closeText}>{closeLabel}</Text>
            </Pressable>
          </View>
        </View>
      </Modal>
    </View>
  );
}

const s = StyleSheet.create({
  label: { fontFamily: fonts.bodyMedium, fontSize: 14, color: colors.text },
  field: { minHeight: TOUCH, flexDirection: "row", alignItems: "center", justifyContent: "space-between", borderWidth: 1, borderColor: colors.border, borderRadius: radius.md, backgroundColor: colors.surface, paddingHorizontal: space.md },
  value: { flex: 1, fontFamily: fonts.body, fontSize: 16, color: colors.text },
  chevron: { fontSize: 16, color: colors.primary, marginLeft: space.sm },
  overlay: { flex: 1, justifyContent: "flex-end", backgroundColor: "rgba(30,27,46,0.45)" },
  sheet: { backgroundColor: colors.surface, borderTopLeftRadius: radius.lg, borderTopRightRadius: radius.lg, padding: space.lg, gap: space.xs },
  sheetTitle: { fontFamily: fonts.heading, fontSize: 16, color: colors.text, marginBottom: space.sm },
  option: { minHeight: TOUCH, flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingHorizontal: space.md, borderRadius: radius.md },
  optionOn: { backgroundColor: colors.bg },
  optionText: { fontFamily: fonts.body, fontSize: 16, color: colors.text },
  check: { fontSize: 18, color: colors.primary },
  close: { minHeight: TOUCH, alignItems: "center", justifyContent: "center", marginTop: space.sm, borderRadius: radius.md, borderWidth: 1, borderColor: colors.primary },
  closeText: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.primary },
});
