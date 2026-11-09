import { describe, expect, it, vi } from "vitest";
import type { StoredMessage } from "../ports";
import { EmailSender, emailSubject, loadEmailConfig, maskEmail, type MailTransport } from "./email-sender";

const msg: StoredMessage = {
  id: "m1", tenantId: "t1", clientId: "c1", channel: "email", address: "anna@example.com",
  text: "Ваш запис підтверджено.", idempotencyKey: "confirmation:a1", kind: "confirmation", status: "queued",
};
const smtpEnv = { EMAIL_MODE: "smtp", SMTP_HOST: "smtp.test", SMTP_PORT: "465", SMTP_SECURE: "true", SMTP_USER: "u", SMTP_PASSWORD: "pw-secret", EMAIL_FROM: "x@test.dev" };
const logger = () => ({ info: vi.fn(), error: vi.fn() });

describe("loadEmailConfig", () => {
  it("defaults_to_mock", () => expect(loadEmailConfig({})).toEqual({ mode: "mock" }));
  it("parses_smtp", () => expect(loadEmailConfig(smtpEnv)).toMatchObject({ mode: "smtp", host: "smtp.test", port: 465, secure: true, from: "x@test.dev" }));
  it("fails_loudly_when-smtp-config-incomplete", () => {
    expect(() => loadEmailConfig({ EMAIL_MODE: "smtp", SMTP_HOST: "h" })).toThrow(/SMTP_PORT.*SMTP_USER.*SMTP_PASSWORD.*EMAIL_FROM/);
  });
  it("rejects_unknown_mode_and_bad_port", () => {
    expect(() => loadEmailConfig({ EMAIL_MODE: "foo" })).toThrow();
    expect(() => loadEmailConfig({ ...smtpEnv, SMTP_PORT: "abc" })).toThrow(/SMTP_PORT/);
  });
});

describe("maskEmail", () => {
  it("masks", () => expect(maskEmail("anna@example.com")).toBe("a***@e***.com"));
  it("handles_garbage", () => expect(maskEmail("nope")).toBe("***"));
});

describe("EmailSender", () => {
  it("mock_logs-only-fact_without-address-or-text", async () => {
    const log = logger();
    await new EmailSender({ mode: "mock" }, log).send(msg);
    const logged = JSON.stringify(log.info.mock.calls);
    expect(log.info).toHaveBeenCalledOnce();
    expect(logged).toContain("m1");
    expect(logged).not.toContain("anna");
    expect(logged).not.toContain("підтверджено");
  });
  it("smtp_sends-plain-text_to-address_with-subject-by-kind", async () => {
    const transport: MailTransport = { sendMail: vi.fn(async () => ({ messageId: "<id@x>" })) };
    const log = logger();
    await new EmailSender(loadEmailConfig(smtpEnv), log, transport).send(msg);
    expect(transport.sendMail).toHaveBeenCalledWith({ from: "x@test.dev", to: "anna@example.com", subject: emailSubject("confirmation"), text: msg.text });
    expect(JSON.stringify(log.info.mock.calls)).not.toContain("anna");
    expect(JSON.stringify(log.info.mock.calls)).not.toContain("pw-secret");
  });
  it("smtp_5xx-is-permanent_network-error-is-retryable_and-no-secrets-in-error", async () => {
    const mk = (err: object) => new EmailSender(loadEmailConfig(smtpEnv), logger(), { sendMail: async () => { throw Object.assign(new Error("auth pw-secret anna@example.com"), err); } });
    await expect(mk({ responseCode: 550 }).send(msg)).rejects.toMatchObject({ permanent: true });
    const e = await mk({ code: "ECONNECTION" }).send(msg).catch((x) => x);
    expect(e.permanent).toBe(false);
    expect(e.message).not.toContain("pw-secret");
    expect(e.message).not.toContain("anna");
  });
  it("rejects_invalid-recipient_permanently", async () => {
    await expect(new EmailSender({ mode: "mock" }, logger()).send({ ...msg, address: "42" })).rejects.toMatchObject({ permanent: true });
  });
});
