import { useEffect, useRef, useState, type ComponentRef } from "react";
import { KeyboardAvoidingView, Modal, Pressable, StyleSheet, Text, TextInput, View, type TextInputProps } from "react-native";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

/**
 * Панель вводу над клавіатурою: поле на екрані лише показує значення, а редагування йде в окремій панелі,
 * яка завжди стоїть над клавіатурою (KeyboardAvoidingView у Modal), тож клавіатура ніколи не перекриває те, що вводиш.
 * «Готово» (або Enter на клавіатурі) переносить текст у поле, «Скасувати» / тап поза панеллю / Android back - відкидають.
 */
export function InputSheet({
  visible,
  label,
  initialValue,
  inputProps,
  doneLabel,
  cancelLabel,
  onDone,
  onCancel,
}: {
  visible: boolean;
  label: string;
  initialValue: string;
  /** Ті самі пропси, що мало б поле (keyboardType, autoComplete, maxLength...). value/onChangeText ігноруються. */
  inputProps: TextInputProps;
  doneLabel: string;
  cancelLabel: string;
  onDone: (value: string, submitted: boolean) => void;
  onCancel: () => void;
}) {
  const [draft, setDraft] = useState(initialValue);
  const input = useRef<ComponentRef<typeof TextInput>>(null);

  // кожне відкриття починається з поточного значення поля
  useEffect(() => {
    if (visible) setDraft(initialValue);
  }, [visible, initialValue]);

  const { value: _v, onChangeText: _c, onSubmitEditing: _s, onBlur: _b, onFocus: _f, style: _st, ...rest } = inputProps;

  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onCancel} statusBarTranslucent onShow={() => input.current?.focus()}>
      {/* behavior "padding" на обох платформах: KAV рахує лише реальне перекриття, тому не подвоює відступ, якщо вікно вже зменшилось */}
      <KeyboardAvoidingView behavior="padding" style={s.overlay}>
        <Pressable accessibilityLabel={cancelLabel} accessibilityRole="button" style={StyleSheet.absoluteFill} onPress={onCancel} />
        <View style={s.panel} accessibilityViewIsModal>
          <Text style={s.label}>{label}</Text>
          <TextInput
            ref={input}
            accessibilityLabel={label}
            placeholderTextColor={colors.muted}
            returnKeyType="done"
            {...rest}
            autoFocus
            value={draft}
            onChangeText={setDraft}
            onSubmitEditing={() => onDone(draft, true)}
            submitBehavior={rest.multiline ? "newline" : "submit"}
            style={[s.input, rest.multiline ? s.multiline : null]}
          />
          <View style={s.actions}>
            <Pressable accessibilityRole="button" onPress={onCancel} style={[s.btn, s.btnSecondary]}>
              <Text style={[s.btnText, { color: colors.primary }]}>{cancelLabel}</Text>
            </Pressable>
            <Pressable accessibilityRole="button" onPress={() => onDone(draft, false)} style={[s.btn, s.btnPrimary]}>
              <Text style={[s.btnText, { color: colors.onPrimary }]}>{doneLabel}</Text>
            </Pressable>
          </View>
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

const s = StyleSheet.create({
  overlay: { flex: 1, justifyContent: "flex-end", backgroundColor: "rgba(30,27,46,0.45)" },
  panel: { backgroundColor: colors.surface, borderTopLeftRadius: radius.lg, borderTopRightRadius: radius.lg, padding: space.lg, gap: space.sm },
  label: { fontFamily: fonts.bodyMedium, fontSize: 14, color: colors.text },
  input: { minHeight: TOUCH, borderWidth: 1, borderColor: colors.primary, borderRadius: radius.md, backgroundColor: colors.surface, paddingHorizontal: space.md, fontFamily: fonts.body, fontSize: 16, color: colors.text },
  multiline: { minHeight: TOUCH * 2, paddingVertical: space.sm, textAlignVertical: "top" },
  actions: { flexDirection: "row", gap: space.sm },
  btn: { flex: 1, minHeight: TOUCH, borderRadius: radius.md, alignItems: "center", justifyContent: "center" },
  btnPrimary: { backgroundColor: colors.primary },
  btnSecondary: { borderWidth: 1, borderColor: colors.primary, backgroundColor: colors.surface },
  btnText: { fontFamily: fonts.bodyMedium, fontSize: 16 },
});
