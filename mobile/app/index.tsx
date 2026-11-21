import { useCallback, useEffect, useRef, useState } from "react";
import { View } from "react-native";
import { Link, useLocalSearchParams } from "expo-router";
import * as Location from "expo-location";
import { useTranslation } from "react-i18next";
import { BookingApiError, humanizeBookingError } from "@beauty/booking-core";
import { catalogApi, localStore } from "@/services";
import { isNotFound, type NearbyItem, type LocationRef } from "@/api/catalog";
import { resolveStart, type GeoPermission } from "@/lib/locationSource";
import { parseLocationInput, formatPublicCode } from "@/lib/deeplink";
import { linkConfig } from "@/config";
import { toResolved, useChooseLocation } from "@/features/location/choose";
import { ScanModal } from "@/features/location/ScanModal";
import { Body, Button, ErrorText, Field, H1, Row, Screen, Spinner } from "@/ui";
import { space } from "@/theme";

/** Крок 1: заклад. Джерело (ADR-021 §2): збережена -> явний тап nearby (дозвіл лише тут) -> місто/код/QR. Deep link обробляє app/l/[code]. */
export default function LocationScreen() {
  const { t } = useTranslation();
  const { change } = useLocalSearchParams<{ change?: string }>();
  const choose = useChooseLocation();

  const [busy, setBusy] = useState(true);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nearby, setNearby] = useState<NearbyItem[]>([]);
  const [cityResults, setCityResults] = useState<LocationRef[] | null>(null);
  const [city, setCity] = useState("");
  const [code, setCode] = useState("");
  const [scanning, setScanning] = useState(false);
  const started = useRef(false);

  const runNearby = useCallback(async () => {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const perm = await Location.requestForegroundPermissionsAsync();
      if (perm.status !== "granted") {
        setMessage(t("location.geoDenied"));
        return;
      }
      // координати живуть лише в цій змінній: у сховище/логи/стан не потрапляють; Low + округлення до 3 знаків у catalogApi.nearby (M2)
      const pos = await Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.Low });
      const items = await catalogApi.nearby(pos.coords.latitude, pos.coords.longitude);
      setNearby(items);
      if (items.length === 0) setMessage(t("location.nearbyEmpty"));
    } catch (e) {
      setError(e instanceof BookingApiError ? humanizeBookingError(e) : t("location.geoUnavailable"));
    } finally {
      setBusy(false);
    }
  }, [t]);

  const start = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const p = await Location.getForegroundPermissionsAsync();
      const geo: GeoPermission = p.status === "granted" ? "granted" : p.status === "denied" && !p.canAskAgain ? "denied" : "undetermined";
      const saved = await localStore.getLastLocation();
      const outcome = await resolveStart(
        { deepLinkCode: null, savedCode: saved?.publicCode ?? null, geo, changing: !!change },
        { resolve: (c) => catalogApi.resolve(c).then(toResolved), clearSaved: () => localStore.clearLastLocation(), isNotFound },
      );
      if (outcome.kind === "resolved") return void (await choose(outcome.location, "replace"));
      // геодозвіл лише після явного тапу «Знайти найближчий заклад» (runNearby), не автоматично при старті
      if (outcome.kind === "manual") setMessage(t("location.geoDenied"));
    } catch (e) {
      setError(humanizeBookingError(e));
    }
    setBusy(false);
  }, [change, choose, t]);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void start();
  }, [start]);

  const openByInput = async (raw: string) => {
    const c = parseLocationInput(raw, linkConfig);
    if (!c) return setError(t("location.codeInvalid"));
    setBusy(true);
    setError(null);
    try {
      await choose(toResolved(await catalogApi.resolve(c)));
    } catch (e) {
      setError(isNotFound(e) ? t("location.notFound") : humanizeBookingError(e));
    } finally {
      setBusy(false);
    }
  };

  const searchCity = async () => {
    if (city.trim().length < 2) return;
    setBusy(true);
    setError(null);
    try {
      setCityResults(await catalogApi.searchByCity(city));
    } catch (e) {
      setError(humanizeBookingError(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Screen>
      <H1>{t("location.title")}</H1>
      {busy && nearby.length === 0 ? <Spinner /> : null}
      {message ? <Body muted>{message}</Body> : null}
      {error ? <ErrorText>{error}</ErrorText> : null}

      {nearby.length > 0 ? (
        <View style={{ gap: space.sm }}>
          <Body muted>{t("location.nearbyTitle")}</Body>
          {nearby.map((n) => (
            <Row key={n.publicCode} title={n.name} subtitle={n.address ?? n.city ?? undefined} right={`${n.distanceKm.toFixed(1)} ${t("location.km")}`} onPress={() => void choose(toResolved(n))} />
          ))}
        </View>
      ) : null}

      <Button title={t("location.findNearby")} variant="secondary" onPress={() => void runNearby()} disabled={busy} />

      <View style={{ gap: space.sm }}>
        <Field label={t("location.cityLabel")} placeholder={t("location.cityPlaceholder")} value={city} onChangeText={setCity} returnKeyType="search" onSubmitEditing={() => void searchCity()} />
        <Button title={t("location.searchCity")} variant="secondary" onPress={() => void searchCity()} disabled={busy || city.trim().length < 2} />
        {cityResults?.length === 0 ? <Body muted>{t("location.searchEmpty")}</Body> : null}
        {cityResults?.map((r) => (
          <Row key={r.publicCode} title={r.name} subtitle={r.address ?? r.city ?? undefined} onPress={() => void choose(toResolved(r))} />
        ))}
      </View>

      <View style={{ gap: space.sm }}>
        <Field label={t("location.codeLabel")} placeholder={t("location.codePlaceholder")} value={code} onChangeText={setCode} autoCapitalize="characters" autoCorrect={false} returnKeyType="go" onSubmitEditing={() => void openByInput(code)} />
        <Button title={t("location.codeOpen")} onPress={() => void openByInput(code)} disabled={busy || code.trim().length === 0} />
        <Button title={t("location.scanQr")} variant="secondary" onPress={() => setScanning(true)} />
      </View>

      <Link href="/appointments" style={{ minHeight: 44, paddingVertical: 12, textAlign: "center" }}>
        {t("common.myAppointments")}
      </Link>
      <Link href="/order" style={{ minHeight: 44, paddingVertical: 12, textAlign: "center" }}>
        {t("shop.myOrders")}
      </Link>

      <ScanModal
        visible={scanning}
        onClose={() => setScanning(false)}
        onData={(d) => {
          setScanning(false);
          const c = parseLocationInput(d, linkConfig);
          if (c) setCode(formatPublicCode(c));
          void openByInput(d);
        }}
      />
    </Screen>
  );
}
