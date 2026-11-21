import { useState } from "react";
import { FlatList, Image, Modal, Pressable, Text, View, useWindowDimensions } from "react-native";
import { useTranslation } from "react-i18next";
import type { PublicProduct } from "@/api/productOrders";
import { colors, fonts, radius, space } from "@/theme";

/** Фото першого елемента для списку; без фото/при помилці (404) - заглушка. Зображення кешуються диском/ОС (cache: force-cache). */
export function galleryOf(p: { imageUrl?: PublicProduct["imageUrl"]; images?: PublicProduct["images"] }): { url: string; thumbUrl: string }[] {
  if (p.images?.length) return p.images;
  return p.imageUrl ? [{ url: p.imageUrl, thumbUrl: p.imageUrl }] : [];
}

function Photo({ uri, size, label, h }: { uri?: string; size: number; label: string; h?: number }) {
  const [failed, setFailed] = useState(false);
  const style = { width: size, height: h ?? size, borderRadius: radius.md, backgroundColor: colors.border };
  if (!uri || failed) {
    return <View accessible accessibilityRole="image" accessibilityLabel={label} style={[style, { alignItems: "center", justifyContent: "center" }]}><Text style={{ color: colors.muted }}>▢</Text></View>;
  }
  return (
    <Image
      source={{ uri, cache: "force-cache" }}
      style={style}
      resizeMode="cover"
      accessible
      accessibilityRole="image"
      accessibilityLabel={label}
      onError={() => setFailed(true)}
    />
  );
}

export function ProductThumb({ product }: { product: PublicProduct }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const g = galleryOf(product);
  const thumb = <Photo uri={product.imageUrl ?? g[0]?.thumbUrl} size={56} label={g.length ? t("shop.photo", { name: product.name }) : t("shop.photoNone", { name: product.name })} />;
  if (!g.length) return thumb;
  return (
    <>
      <Pressable accessibilityRole="button" accessibilityLabel={t("shop.photoOpen", { name: product.name })} onPress={() => setOpen(true)}>
        {thumb}
      </Pressable>
      {open ? <ProductGallery name={product.name} images={g} onClose={() => setOpen(false)} /> : null}
    </>
  );
}

/** Галерея: горизонтальний pager повнорозмірних фото (url, 1600 px). */
export function ProductGallery({ name, images, onClose }: { name: string; images: { url: string }[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { width } = useWindowDimensions();
  const w = width - space.md * 2;
  return (
    <Modal transparent animationType="fade" onRequestClose={onClose}>
      <View style={{ flex: 1, backgroundColor: "rgba(0,0,0,0.85)", justifyContent: "center", padding: space.md }}>
        <FlatList
          data={images}
          horizontal
          pagingEnabled
          showsHorizontalScrollIndicator={false}
          keyExtractor={(i, idx) => `${idx}-${i.url}`}
          getItemLayout={(_, index) => ({ length: w, offset: w * index, index })}
          style={{ flexGrow: 0 }}
          renderItem={({ item, index }) => <Photo uri={item.url} size={w} label={t("shop.photoN", { n: index + 1, total: images.length, name })} />}
        />
        <Pressable accessibilityRole="button" accessibilityLabel={t("shop.close")} onPress={onClose} style={{ alignSelf: "center", marginTop: space.md, padding: space.md }}>
          <Text style={{ fontFamily: fonts.bodyMedium, color: "#fff", fontSize: 16 }}>{t("shop.close")}</Text>
        </Pressable>
      </View>
    </Modal>
  );
}
