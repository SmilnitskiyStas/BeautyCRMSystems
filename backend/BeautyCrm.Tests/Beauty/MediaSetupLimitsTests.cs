using BeautyCrm.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeautyCrm.Tests.Beauty;

/// <summary>Security-рев'ю M1/m6: глобальний ліміт upload, бюджет пам'яті й діапазон PublicPermitPerMinute.</summary>
public sealed class MediaSetupLimitsTests
{
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static void Configure(Dictionary<string, string?> values) =>
        new ServiceCollection().AddBeautyMediaApi(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new Env());

    [Fact]
    public void Upload_gate_rejects_beyond_the_limit_without_waiting_and_frees_on_exit()
    {
        var t = new UploadThrottle(20, 2);
        Assert.True(t.TryEnter()); Assert.True(t.TryEnter());
        Assert.False(t.TryEnter());
        t.Exit();
        Assert.True(t.TryEnter());
    }

    [Fact]
    public void Memory_budget_is_checked_at_startup()
    {
        Configure(new() { ["Media:MaxPixels"] = "25000000", ["Media:MaxConcurrentProcessing"] = "1" });
        Assert.Throws<InvalidOperationException>(() => Configure(new() { ["Media:MaxPixels"] = "25000000", ["Media:MaxConcurrentProcessing"] = "2" }));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("100001")]
    public void Public_permit_per_minute_is_range_validated(string v) =>
        Assert.Throws<InvalidOperationException>(() => Configure(new() { ["Media:PublicPermitPerMinute"] = v }));
}
