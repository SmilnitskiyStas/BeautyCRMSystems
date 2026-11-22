import { useEffect, useState } from "react";
import { Image, StyleSheet, Text, View } from "react-native";
import { initials } from "@/lib/specialist";
import { colors, fonts } from "@/theme";

/** Логотип закладу (заокруглений квадрат): https-зображення, а за відсутності/помилки завантаження - ініціали. Декоративний (назва поруч). */
export function LocationLogo({ name, logoUrl, size = 40 }: { name: string; logoUrl?: string | null; size?: number }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [logoUrl]);
  const box = { width: size, height: size, borderRadius: Math.round(size * 0.25) };
  if (logoUrl && !failed) {
    return (
      <Image
        accessible={false}
        source={{ uri: logoUrl }}
        onError={() => setFailed(true)}
        resizeMode="cover"
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
