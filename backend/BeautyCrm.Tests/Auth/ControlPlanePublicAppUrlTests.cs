using BeautyCrm.Application.Features.BeautyControlPlane;

namespace BeautyCrm.Tests.Auth;

/// <summary>TASK-826 (L2): поза Development PublicAppUrl лише https (токен запрошення йде в посиланні).</summary>
public sealed class ControlPlanePublicAppUrlTests
{
    [Theory]
    [InlineData("https://app.example.test", false, true)]
    [InlineData("https://app.example.test", true, true)]
    [InlineData("http://app.example.test", false, false)]
    [InlineData("http://localhost:3000", false, false)]
    [InlineData("http://localhost:3000", true, true)]
    [InlineData("ftp://app.example.test", true, false)]
    [InlineData("app.example.test", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, false, false)]
    public void http_is_allowed_only_when_development(string? url, bool allowHttp, bool expected) =>
        Assert.Equal(expected, ControlPlaneService.IsAllowedPublicAppUrl(url, allowHttp));
}
