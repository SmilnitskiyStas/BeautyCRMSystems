import { useState } from "react";
import { Image, StyleSheet, Text, View } from "react-native";
import { initials } from "@/lib/specialist";
import { colors, fonts } from "@/theme";

/** Круглий аватар: фото (https), а за відсутності/помилки завантаження - коло з ініціалами. Декоративний (ім'я поруч). */
export function Avatar({ name, photoUrl, size = 56 }: { name: string; photoUrl?: string | null; size?: number }) {
  const [failed, setFailed] = useState(false);
  const box = { width: size, height: size, borderRadius: size / 2 };
  if (photoUrl && !failed) {
    return (
      <Image
        accessible={false}
        source={{ uri: photoUrl }}
        onError={() => setFailed(true)}
        style={[box, { backgroundColor: colors.border }]}
      />
    );
  }
  return (
    <View accessible={false} style={[box, s.fallback]}>
      <Text style={[s.text, { fontSize: size * 0.36 }]}>{initials(name)}</Text>
    </View>
  );
}

const s = StyleSheet.create({
  fallback: { backgroundColor: colors.primary, alignItems: "center", justifyContent: "center" },
  text: { fontFamily: fonts.bodyMedium, color: colors.onPrimary },
});
