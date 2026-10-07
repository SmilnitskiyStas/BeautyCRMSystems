using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BeautyCrm.Infrastructure.Data;

/// <summary>Maps enum members to their [EnumMember(Value)] literal (e.g. NoShow -> "no_show").</summary>
public static class EnumText<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToDbMap = typeof(TEnum)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .ToDictionary(
            f => (TEnum)f.GetValue(null)!,
            f => f.GetCustomAttribute<EnumMemberAttribute>()?.Value
                 ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{f.Name} has no [EnumMember(Value)]"));

    private static readonly Dictionary<string, TEnum> FromDbMap = ToDbMap.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static IReadOnlyCollection<string> Values => ToDbMap.Values;

    public static string ToDb(TEnum value) => ToDbMap[value];

    public static TEnum FromDb(string value) =>
        FromDbMap.TryGetValue(value, out var e)
            ? e
            : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} value '{value}' in database");

    /// <summary>SQL for a CHECK constraint: <c>column IN ('a', 'b')</c>.</summary>
    public static string CheckSql(string column) =>
        $"{column} IN ({string.Join(", ", Values.Select(v => $"'{v}'"))})";
}

public sealed class EnumMemberConverter<TEnum> : ValueConverter<TEnum, string> where TEnum : struct, Enum
{
    public EnumMemberConverter()
        : base(v => EnumText<TEnum>.ToDb(v), s => EnumText<TEnum>.FromDb(s))
    {
    }
}
