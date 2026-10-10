"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRef } from "react";
import { bookingApi } from "../api";
import { useTenant } from "../components/tenant-context";
import { createKeyHolder } from "../idempotency";
import type { CreateAppointmentRequest } from "../types";

export const useLocations = () => {
  const tenant = useTenant();
  return useQuery({ queryKey: ["booking", tenant, "locations"], queryFn: () => bookingApi.getLocations(tenant) });
};

export const useSpecialists = (locationId?: string) => {
  const tenant = useTenant();
  return useQuery({
    queryKey: ["booking", tenant, "specialists", locationId],
    queryFn: () => bookingApi.getSpecialists(tenant, locationId!),
    enabled: !!locationId,
  });
};

export const useServices = (locationId?: string, specialistId?: string) => {
  const tenant = useTenant();
  return useQuery({
    queryKey: ["booking", tenant, "services", locationId, specialistId],
    queryFn: () => bookingApi.getServices(tenant, locationId!, specialistId!),
    enabled: !!locationId && !!specialistId,
  });
};

export const useSlots = (q: { locationId?: string; specialistId?: string; serviceId?: string; date: string; enabled?: boolean }) => {
  const tenant = useTenant();
  return useQuery({
    queryKey: ["booking", tenant, "slots", { locationId: q.locationId, specialistId: q.specialistId, serviceId: q.serviceId, date: q.date }],
    queryFn: () =>
      bookingApi.getSlots(tenant, {
        locationId: q.locationId!,
        specialistId: q.specialistId!,
        serviceId: q.serviceId!,
        date: q.date,
      }),
    enabled: !!q.locationId && !!q.specialistId && !!q.serviceId && q.enabled !== false,
    // Слоти швидко застарівають (їх можуть зайняти): не кешуємо між відвідинами кроку.
    staleTime: 0,
  });
};

/** Створення запису з Idempotency-Key: повтор того самого тіла (мережевий збій) йде з тим самим ключем. */
export const useCreateAppointment = () => {
  const tenant = useTenant();
  const qc = useQueryClient();
  const keys = useRef(createKeyHolder());
  return useMutation({
    mutationFn: (req: CreateAppointmentRequest) => bookingApi.createAppointment(tenant, req, keys.current.keyFor(req)),
    onSuccess: () => keys.current.clear(),
    // Після помилки слота список слотів застарів.
    onError: () => void qc.invalidateQueries({ queryKey: ["booking", tenant, "slots"] }),
  });
};

export const useAppointment = (token: string) => {
  const tenant = useTenant();
  return useQuery({
    queryKey: ["booking", tenant, "appointment", token],
    queryFn: () => bookingApi.getAppointment(tenant, token),
    retry: false,
    staleTime: 0,
  });
};

export const useCancelAppointment = (token: string) => {
  const tenant = useTenant();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => bookingApi.cancelAppointment(tenant, token),
    onSuccess: (r) => qc.setQueryData(["booking", tenant, "appointment", token], r.appointment),
  });
};
