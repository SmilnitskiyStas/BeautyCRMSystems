import { Platform } from "react-native";
import * as SecureStore from "expo-secure-store";
import * as Crypto from "expo-crypto";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { createHttpBookingApi, type FetchLike } from "@beauty/booking-core";
import { config } from "@/config";
import { createCatalogApi, type CatalogFetch } from "@/api/catalog";
import { createProductOrdersApi } from "@/api/productOrders";
import { createLocalStore } from "@/storage/tokenStore";
import { createOrderSubmitter } from "@/features/shop/submit";
import { createBookingSubmitter } from "@/features/booking/submit";
import { withUpgradeGuard } from "@/lib/upgrade";
import { useUpgrade } from "@/state/upgrade";

/** ADR-022 §3: мітка клієнта й версія - НЕ автентифікація, а метрики/kill-switch (426). */
const clientHeaders = {
  "X-Booking-Client": Platform.OS === "ios" ? "ios" : "android",
  "X-App-Version": config.appVersion,
};

const onUpgrade = () => useUpgrade.getState().setRequired();

const bookingFetch: FetchLike = withUpgradeGuard(
  (url, init) => fetch(url, { ...init, headers: { ...init.headers, ...clientHeaders } }) as ReturnType<FetchLike>,
  onUpgrade,
);

const catalogFetch: CatalogFetch = withUpgradeGuard(
  (url, init) => fetch(url, { ...init, headers: { ...init.headers, ...clientHeaders } }) as ReturnType<CatalogFetch>,
  onUpgrade,
);

export const productOrdersApi = createProductOrdersApi({ baseUrl: config.apiBaseUrl, fetch: catalogFetch });
export const bookingApi =createHttpBookingApi({ baseUrl: config.apiBaseUrl, fetch: bookingFetch });
export const catalogApi = createCatalogApi({ baseUrl: config.apiBaseUrl, fetch: catalogFetch });

const SECURE_OPTS: SecureStore.SecureStoreOptions = { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY };

export const localStore = createLocalStore(
  {
    // L8: лише розблокований пристрій, без міграції в iCloud/на інший пристрій
    getItemAsync: (k) => SecureStore.getItemAsync(k, SECURE_OPTS),
    setItemAsync: (k, v) => SecureStore.setItemAsync(k, v, SECURE_OPTS),
    deleteItemAsync: (k) => SecureStore.deleteItemAsync(k, SECURE_OPTS),
  },
  AsyncStorage,
);

export const orderSubmitter = createOrderSubmitter({ api: productOrdersApi, store: localStore, generateKey: () => Crypto.randomUUID() });

/** Ключ ідемпотентності: UUID v4 з expo-crypto (core не залежить від платформи). */
export const bookingSubmitter = createBookingSubmitter({
  api: bookingApi,
  store: localStore,
  generateKey: () => Crypto.randomUUID(),
});
