import "@/i18n";
import { useEffect, useState } from "react";
import { Stack } from "expo-router";
import { StatusBar } from "expo-status-bar";
import * as SplashScreen from "expo-splash-screen";
import { useFonts } from "expo-font";
import { Onest_400Regular, Onest_600SemiBold } from "@expo-google-fonts/onest";
import { Unbounded_600SemiBold } from "@expo-google-fonts/unbounded";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { useTranslation } from "react-i18next";
import { colors, fonts } from "@/theme";
import { localStore } from "@/services";
import { useUpgrade } from "@/state/upgrade";
import { Body, H1, Screen } from "@/ui";

void SplashScreen.preventAutoHideAsync();

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 30_000, refetchOnWindowFocus: false } },
});

export default function RootLayout() {
  const { t } = useTranslation();
  const [loaded, error] = useFonts({ Onest_400Regular, Onest_600SemiBold, Unbounded_600SemiBold });

  const upgradeRequired = useUpgrade((u) => u.required);
  const [cleaned, setCleaned] = useState(false);

  // L8: залишки Keychain після перевстановлення стираються до першого читання профілю/токенів
  useEffect(() => {
    localStore.firstRunCleanup().catch(() => undefined).finally(() => setCleaned(true));
  }, []);

  useEffect(() => {
    if (loaded || error) void SplashScreen.hideAsync();
  }, [loaded, error]);

  if ((!loaded && !error) || !cleaned) return null;

  // 426: версія більше не підтримується - далі записатись не можна
  if (upgradeRequired) {
    return (
      <SafeAreaProvider>
        <StatusBar style="dark" />
        <Screen>
          <H1>{t("upgrade.title")}</H1>
          <Body>{t("upgrade.body")}</Body>
        </Screen>
      </SafeAreaProvider>
    );
  }

  return (
    <SafeAreaProvider>
      <QueryClientProvider client={queryClient}>
        <StatusBar style="dark" />
        <Stack
          screenOptions={{
            headerStyle: { backgroundColor: colors.bg },
            headerTintColor: colors.primary,
            headerTitleStyle: { fontFamily: fonts.bodyMedium, color: colors.text },
            headerShadowVisible: false,
            contentStyle: { backgroundColor: colors.bg },
            headerBackTitle: t("common.back"),
          }}
        >
          <Stack.Screen name="index" options={{ title: "Beauty" }} />
          <Stack.Screen name="l/[code]" options={{ title: "Beauty" }} />
          <Stack.Screen name="service" options={{ title: t("service.title") }} />
          <Stack.Screen name="specialist" options={{ title: t("specialist.title") }} />
          <Stack.Screen name="reviews" options={{ title: t("reviews.title"), presentation: "modal" }} />
          <Stack.Screen name="time" options={{ title: t("time.title") }} />
          <Stack.Screen name="checkout" options={{ title: t("checkout.title") }} />
          <Stack.Screen name="appointments" options={{ title: t("appointments.title") }} />
        </Stack>
      </QueryClientProvider>
    </SafeAreaProvider>
  );
}
