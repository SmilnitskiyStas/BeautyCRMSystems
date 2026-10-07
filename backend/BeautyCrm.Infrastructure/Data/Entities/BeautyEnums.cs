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
}
