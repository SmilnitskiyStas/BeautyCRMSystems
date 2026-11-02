import type { ChannelSenderPort, StoredMessage } from "../ports";
import { consoleLogger, type Logger } from "./pg-support";

/**
 * ChannelSenderPort for `Channels__UseMocks=true` (Development only): the same switch as the API's mock adapters.
 * Makes NO network call and never logs the message text or any address/token: only ids and the channel.
 */
export class MockChannelSender implements ChannelSenderPort {
  readonly sent: { id: string; channel: string }[] = [];
  constructor(private readonly logger: Logger = consoleLogger) {}

  async send(msg: StoredMessage): Promise<void> {
    this.sent.push({ id: msg.id, channel: msg.channel });
    this.logger.info("mock channel send (no network)", { messageId: msg.id, tenantId: msg.tenantId, channel: msg.channel });
  }
}

/** `Channels__UseMocks` is honoured by the worker only in Development (mirrors the API startup rule). */
export function useMockChannels(env: NodeJS.ProcessEnv): boolean {
  if (!/^true$/i.test(env["Channels__UseMocks"] ?? "")) return false;
  if (env["ASPNETCORE_ENVIRONMENT"] !== "Development") {
    throw new Error("Channels__UseMocks=true is allowed only with ASPNETCORE_ENVIRONMENT=Development");
  }
  return true;
}
