import type { Logger } from "../adapters/pg-support";

export interface ScheduledTask { name: string; everyMs: number; runOnStart?: boolean; run: () => Promise<unknown> }

/** Repeating polling schedule; a task never overlaps itself, failures are logged and retried on the next tick. */
export class Scheduler {
  private timers: NodeJS.Timeout[] = [];
  private running = new Set<string>();
  private inflight = new Set<Promise<void>>();

  constructor(private readonly tasks: ScheduledTask[], private readonly logger: Logger) {}

  start(): void {
    for (const t of this.tasks) {
      this.timers.push(setInterval(() => this.tick(t), t.everyMs));
      if (t.runOnStart ?? true) this.tick(t);
    }
  }

  tick(t: ScheduledTask): Promise<void> {
    if (this.running.has(t.name)) return Promise.resolve();
    this.running.add(t.name);
    const p = t.run().then(
      (r) => this.logger.info("task done", { task: t.name, result: r as Record<string, unknown> }),
      (err) => this.logger.error("task failed", { task: t.name, error: String(err) }),
    ).finally(() => { this.running.delete(t.name); this.inflight.delete(p); });
    this.inflight.add(p);
    return p;
  }

  async stop(): Promise<void> {
    this.timers.forEach(clearInterval);
    this.timers = [];
    await Promise.all(this.inflight);
  }
}
