import { Queue, Worker, type ConnectionOptions } from "bullmq";
import type { BeautyDeps, QueuePort } from "../ports";
import { OUTBOX_BACKOFF_MS, OUTBOX_MAX_ATTEMPTS, QUEUES } from "./beauty-common";
import { processOutbox } from "./beauty-outbox";
import { processReminder } from "./beauty-reminder";
import { processCampaign } from "./beauty-campaign";
import { processWinback } from "./beauty-winback";
import { processReviewRequest } from "./beauty-review";

/** BullMQ-backed QueuePort. jobId dedups at queue level too. */
export function createBullQueuePort(connection: ConnectionOptions): QueuePort {
  const outbox = new Queue(QUEUES.outbox, { connection });
  const reminder = new Queue(QUEUES.reminder, { connection });
  const campaign = new Queue(QUEUES.campaign, { connection });
  return {
    enqueueOutbox: async (messageId) => {
      await outbox.add("send", { messageId }, {
        jobId: messageId, attempts: OUTBOX_MAX_ATTEMPTS,
        backoff: { type: "exponential", delay: OUTBOX_BACKOFF_MS }, removeOnComplete: 1000,
      });
    },
    enqueueReminder: async (data, delay) => {
      await reminder.add("send", data, { jobId: `${data.appointmentId}-${data.offset}`, delay });
    },
    enqueueCampaign: async (id, delay) => {
      await campaign.add("run", { campaignId: id }, { delay });
    },
  };
}

export function registerBeautyWorkers(connection: ConnectionOptions, deps: BeautyDeps): Worker[] {
  return [
    new Worker(QUEUES.outbox, (job) => processOutbox(deps, job.data, job.attemptsMade), { connection }),
    new Worker(QUEUES.reminder, (job) => processReminder(deps, job.data), { connection }),
    new Worker(QUEUES.campaign, (job) => processCampaign(deps, job.data), { connection }),
    new Worker(QUEUES.winback, () => processWinback(deps), { connection }),
    new Worker(QUEUES.review, (job) => processReviewRequest(deps, job.data), { connection }),
  ];
}
