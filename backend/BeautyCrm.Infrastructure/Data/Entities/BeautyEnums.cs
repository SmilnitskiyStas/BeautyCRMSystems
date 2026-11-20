using System.Runtime.Serialization;

namespace BeautyCrm.Infrastructure.Data.Entities;

// Enums are stored as text + CHECK constraint (not PG enum types): adding a value later is an
// additive migration (replace the CHECK), no ALTER TYPE. [EnumMember] value = DB literal.

public enum AppointmentStatus
{
    [EnumMember(Value = "pending")] Pending,
    [EnumMember(Value = "confirmed")] Confirmed,
    [EnumMember(Value = "completed")] Completed,
    [EnumMember(Value = "cancelled")] Cancelled,
    [EnumMember(Value = "no_show")] NoShow,
}

public enum AppointmentSource
{
    [EnumMember(Value = "admin")] Admin,
    [EnumMember(Value = "online")] Online,
    [EnumMember(Value = "telegram")] Telegram,
    [EnumMember(Value = "instagram")] Instagram,
}

public enum ReminderOption
{
    [EnumMember(Value = "none")] None,
    [EnumMember(Value = "1h")] OneHour,
    [EnumMember(Value = "2h")] TwoHours,
    /// <summary>TASK-736 (§22.3): будь-який інший набір точок; дзеркало плану reminder_method/reminder_offsets.</summary>
    [EnumMember(Value = "custom")] Custom,
    [EnumMember(Value = "24h")] TwentyFourHours,
}

public enum PaymentMethod
{
    [EnumMember(Value = "card")] Card,
    [EnumMember(Value = "cash")] Cash,
}

public enum PaymentStatus
{
    [EnumMember(Value = "pending")] Pending,
    [EnumMember(Value = "paid")] Paid,
    [EnumMember(Value = "failed")] Failed,
    [EnumMember(Value = "refunded")] Refunded,
}

public enum DiscountType
{
    [EnumMember(Value = "percent")] Percent,
    [EnumMember(Value = "fixed")] Fixed,
}

public enum ChannelType
{
    [EnumMember(Value = "telegram")] Telegram,
    [EnumMember(Value = "instagram")] Instagram,
    [EnumMember(Value = "facebook")] Facebook,
    [EnumMember(Value = "whatsapp")] WhatsApp,
    [EnumMember(Value = "viber")] Viber,
    [EnumMember(Value = "widget")] Widget,
    /// <summary>Email-доставка (підтвердження запису): адреса клієнта — у beauty_conversations.external_chat_id; SMTP — глобальний, без секретів у каналі.</summary>
    [EnumMember(Value = "email")] Email,
}

public enum ConversationStatus
{
    [EnumMember(Value = "open")] Open,
    [EnumMember(Value = "closed")] Closed,
}

public enum MessageDirection
{
    [EnumMember(Value = "inbound")] Inbound,
    [EnumMember(Value = "outbound")] Outbound,
}

public enum MessageSenderType
{
    [EnumMember(Value = "client")] Client,
    [EnumMember(Value = "staff")] Staff,
    [EnumMember(Value = "ai")] Ai,
    [EnumMember(Value = "system")] System,
}

public enum AiActionStatus
{
    [EnumMember(Value = "proposed")] Proposed,
    [EnumMember(Value = "confirmed")] Confirmed,
    [EnumMember(Value = "rejected")] Rejected,
    [EnumMember(Value = "executed")] Executed,
    [EnumMember(Value = "failed")] Failed,
}

public enum ReminderStatus
{
    [EnumMember(Value = "scheduled")] Scheduled,
    [EnumMember(Value = "sent")] Sent,
    [EnumMember(Value = "failed")] Failed,
    [EnumMember(Value = "cancelled")] Cancelled,
    /// <summary>TASK-736 (§22.3): точку не буде надіслано; причина в skip_reason (код).</summary>
    [EnumMember(Value = "skipped")] Skipped,
}

// ---------- Склад (ADR-025, §32.3, TASK-876) ----------

public enum ProductUnit
{
    [EnumMember(Value = "pcs")] Pcs,
    [EnumMember(Value = "ml")] Ml,
    [EnumMember(Value = "g")] G,
    [EnumMember(Value = "l")] L,
    [EnumMember(Value = "kg")] Kg,
    [EnumMember(Value = "pack")] Pack,
}

public enum ProductPurpose
{
    [EnumMember(Value = "sale")] Sale,
    [EnumMember(Value = "salon_use")] SalonUse,
    [EnumMember(Value = "both")] Both,
}

public enum StockOperationKind
{
    [EnumMember(Value = "receipt")] Receipt,
    [EnumMember(Value = "write_off")] WriteOff,
    [EnumMember(Value = "transfer")] Transfer,
    [EnumMember(Value = "stocktake")] Stocktake,
    /// <summary>Етап 2 (автосписання за послугою); CHECK одразу надмножиною.</summary>
    [EnumMember(Value = "service_consumption")] ServiceConsumption,
    /// <summary>Етап 3 (продаж).</summary>
    [EnumMember(Value = "sale")] Sale,
    /// <summary>ADR-026 §2.8: повернення оплаченого замовлення адміном (рухи sale_return у ті самі партії).</summary>
    [EnumMember(Value = "sale_return")] SaleReturn,
}

public enum WriteOffReason
{
    [EnumMember(Value = "expired")] Expired,
    [EnumMember(Value = "damaged")] Damaged,
    [EnumMember(Value = "lost")] Lost,
    [EnumMember(Value = "internal_use")] InternalUse,
    [EnumMember(Value = "correction")] Correction,
    [EnumMember(Value = "other")] Other,
}

public enum StockMovementType
{
    [EnumMember(Value = "receipt")] Receipt,
    [EnumMember(Value = "write_off")] WriteOff,
    [EnumMember(Value = "transfer_out")] TransferOut,
    [EnumMember(Value = "transfer_in")] TransferIn,
    [EnumMember(Value = "stocktake_adjust")] StocktakeAdjust,
    [EnumMember(Value = "service_consumption")] ServiceConsumption,
    [EnumMember(Value = "sale")] Sale,
    /// <summary>ADR-026 §2.8: quantity &gt; 0, та сама партія, що в руху sale; дефіцит не погашає.</summary>
    [EnumMember(Value = "sale_return")] SaleReturn,
}

public enum StocktakeStatus
{
    [EnumMember(Value = "draft")] Draft,
    [EnumMember(Value = "posted")] Posted,
    [EnumMember(Value = "cancelled")] Cancelled,
}

// ---------- Замовлення товарів клієнтами (ADR-026, контракт §33, TASK-901) ----------

public enum ProductOrderStatus
{
    [EnumMember(Value = "pending_payment")] PendingPayment,
    [EnumMember(Value = "paid")] Paid,
    [EnumMember(Value = "fulfilled")] Fulfilled,
    [EnumMember(Value = "cancelled")] Cancelled,
    [EnumMember(Value = "expired")] Expired,
    [EnumMember(Value = "refunded")] Refunded,
}

/// <summary>Мітка клієнта X-Booking-Client (лише метрика).</summary>
public enum ProductOrderSource
{
    [EnumMember(Value = "web")] Web,
    [EnumMember(Value = "ios")] Ios,
    [EnumMember(Value = "android")] Android,
}

public enum ProductOrderCloseReason
{
    [EnumMember(Value = "client")] Client,
    [EnumMember(Value = "staff")] Staff,
    [EnumMember(Value = "expired")] Expired,
    [EnumMember(Value = "stock_unavailable")] StockUnavailable,
}
