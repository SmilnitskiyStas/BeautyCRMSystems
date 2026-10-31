namespace BeautyCrm.Infrastructure.Data.Entities;

/// <summary>
/// Собівартість послуги (TASK-762, §24.3, ADR-014): LocationId == null - мережевий рівень, інакше override закладу на весь рядок.
/// Окрема таблиця від цін: specialist читає ціни, але не собівартість (лише owner/admin).
/// </summary>
public class ServiceCost : TenantEntity
{
    public Guid ServiceId { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>0..1 000 000, 2 знаки.</summary>
    public decimal CostAmount { get; set; }
    /// <summary>Виплата майстру, % від price_final (0..100); null = без виплати.</summary>
    public decimal? MasterPercent { get; set; }
    public Guid UpdatedByUserId { get; set; }

    public Service? Service { get; set; }
    public Location? Location { get; set; }
}

/// <summary>Append-only аудит змін собівартості: лише назви полів, без сум і відсотків.</summary>
public class ServiceCostAuditRow : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>set | delete.</summary>
    public string Action { get; set; } = null!;
    /// <summary>Підмножина {cost_amount, master_percent}.</summary>
    public string[] ChangedFields { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
