"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { bookingApi } from "../api/client";
import type { CreateAppointmentRequest } from "../types";

export const useLocations = () =>
  useQuery({ queryKey: ["booking", "locations"], queryFn: () => bookingApi.getLocations() });

export const useSpecialists = (locationId?: string) =>
  useQuery({
    queryKey: ["booking", "specialists", locationId],
    queryFn: () => bookingApi.getSpecialists(locationId!),
    enabled: !!locationId,
  });

export const useServices = (locationId?: string, specialistId?: string) =>
  useQuery({
    queryKey: ["booking", "services", locationId, specialistId],
    queryFn: () => bookingApi.getServices(locationId!, specialistId!),
    enabled: !!locationId && !!specialistId,
  });

export const useSlots = (q: {
  locationId?: string;
  specialistId?: string;
  serviceId?: string;
  date: string;
}) =>
  useQuery({
    queryKey: ["booking", "slots", q],
    queryFn: () =>
      bookingApi.getSlots({
        locationId: q.locationId!,
        specialistId: q.specialistId!,
        serviceId: q.serviceId!,
        date: q.date,
      }),
    enabled: !!q.locationId && !!q.specialistId && !!q.serviceId,
  });

export const useCreateAppointment = () =>
  useMutation({ mutationFn: (req: CreateAppointmentRequest) => bookingApi.createAppointment(req) });

export const useAppointment = (id: string) =>
  useQuery({
    queryKey: ["booking", "appointment", id],
    queryFn: () => bookingApi.getAppointment(id),
    retry: false,
  });
