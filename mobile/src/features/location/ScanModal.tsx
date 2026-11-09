import { useEffect, useRef } from "react";
import { Modal, StyleSheet, Text, View } from "react-native";
import { CameraView, useCameraPermissions } from "expo-camera";
import { useTranslation } from "react-i18next";
import { Button } from "@/ui";
import { colors, fonts, space } from "@/theme";

/** Сканер QR закладу. Дозвіл камери запитується лише тут, коли користувач сам натиснув «Сканувати». */
export function ScanModal({ visible, onClose, onData }: { visible: boolean; onClose: () => void; onData: (data: string) => void }) {
  const { t } = useTranslation();
  const [perm, request] = useCameraPermissions();
  const handled = useRef(false);

  useEffect(() => {
    if (visible) {
      handled.current = false;
      if (perm && !perm.granted && perm.canAskAgain) void request();
    }
  }, [visible, perm, request]);

  return (
    <Modal visible={visible} animationType="slide" onRequestClose={onClose}>
      <View style={s.wrap}>
        {perm?.granted ? (
          <CameraView
            style={StyleSheet.absoluteFill}
            facing="back"
            barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
            onBarcodeScanned={({ data }) => {
              if (handled.current) return;
              handled.current = true;
              onData(data);
            }}
          />
        ) : null}
        <View style={s.overlay}>
          <Text style={s.hint}>{t("location.scanHint")}</Text>
          <Button title={t("common.cancel")} variant="secondary" onPress={onClose} />
        </View>
      </View>
    </Modal>
  );
}

const s = StyleSheet.create({
  wrap: { flex: 1, backgroundColor: "#000" },
  overlay: { position: "absolute", left: 0, right: 0, bottom: 0, padding: space.xl, gap: space.md },
  hint: { color: "#fff", fontFamily: fonts.bodyMedium, fontSize: 16, textAlign: "center", backgroundColor: colors.text + "99", padding: space.md, borderRadius: 12 },
});
