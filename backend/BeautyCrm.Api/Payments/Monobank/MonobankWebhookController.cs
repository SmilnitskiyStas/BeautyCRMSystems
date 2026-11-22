using System.Text.Json;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Application.Features.BeautyProductOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BeautyCrm.Api.Payments.Monobank;

/// <summary>
/// Вебхук monobank acquiring (ADR-028): POST з X-Sign (ECDSA над сирим тілом). Без JWT; tenant - за slug у шляху (його задає наш webHookUrl),
/// автентичність - лише підпис. Порядок: розмір тіла -> підпис (401) -> розбір (400) -> обробка. Після валідного підпису завжди 200,
/// навіть якщо платіж невідомий (інакше monobank повторює ще до 3 разів без шансу на інший результат). Тіло й підпис не логуються.
/// </summary>
[ApiController]
[Route("api/public/{tenantSlug}/payments/monobank")]
[AllowAnonymous]
[PublicTenant]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class MonobankWebhookController(ProductOrderPaymentNotifications notifications, ILogger<MonobankWebhookController> log) : ControllerBase
{
    public const int MaxBodyBytes = 64 * 1024;

    [HttpPost("webhook")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        // Payments:Provider != monobank -> маршруту наче немає
        if (HttpContext.RequestServices.GetService<MonobankSignature>() is not { } signature) return NotFound();
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, ct);
            body = ms.ToArray();
        }
        if (!await signature.VerifyAsync(body, Request.Headers["X-Sign"].FirstOrDefault(), ct))
        {
            log.LogWarning("payment_webhook_rejected provider={Provider} reason={Reason}", MonobankOptions.ProviderName, "bad_signature");
            return Unauthorized();
        }

        MonobankInvoiceStatus? s;
        try { s = JsonSerializer.Deserialize<MonobankInvoiceStatus>(body, MonobankClient.Json); }
        catch (JsonException) { s = null; }
        if (s is not { InvoiceId: { Length: > 0 } invoiceId } || !Guid.TryParseExact(s.Reference, "N", out var paymentId))
        {
            log.LogWarning("payment_webhook_rejected provider={Provider} reason={Reason}", MonobankOptions.ProviderName, "invalid_body");
            return Ok(); // підпис валідний, але це не наш рахунок (reference іншого формату) - повтор не допоможе
        }

        var outcome = await notifications.HandleAsync(
            new ProviderPaymentNotification(paymentId, invoiceId, MonobankPaymentService.MapState(s.Status), s.Amount), ct);
        log.LogInformation("payment_webhook provider={Provider} payment={PaymentId} status={Status} outcome={Outcome}",
            MonobankOptions.ProviderName, paymentId, s.Status, outcome);
        return Ok();
    }
}

/// <summary>Сторінка повернення після оплати (redirectUrl рахунку): статус не показує, лише веде назад у застосунок (стан - з API).</summary>
[ApiController]
[Route("api/public/payments")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class PaymentReturnController : ControllerBase
{
    [HttpGet("return")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    public IActionResult Return()
    {
        if (HttpContext.RequestServices.GetService<MonobankOptions>() is not { } options) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        var link = System.Net.WebUtility.HtmlEncode(options.AppReturnUrl);
        var html = $$"""
            <!DOCTYPE html>
            <html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="refresh" content="0;url={{link}}"><title>Оплата</title>
            <style>body{font-family:system-ui,sans-serif;max-width:28rem;margin:3rem auto;padding:0 1rem;text-align:center}a{display:inline-block;margin-top:1rem;padding:.75rem 1.25rem;border-radius:.5rem;background:#111;color:#fff;text-decoration:none}</style>
            </head><body><h1>Оплату завершено</h1><p>Поверніться в застосунок: статус замовлення оновиться автоматично.</p>
            <a href="{{link}}">Повернутися в застосунок</a></body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }
}
