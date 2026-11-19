using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyStaff;

namespace BeautyCrm.Tests.Beauty;

/// <summary>Публічний профіль майстра в адмінці: photoUrl (лише https), рік практики, bio; семантика «null = без змін» (юніт, без БД).</summary>
public class StaffPublicProfileTests
{
    private static readonly DateTimeOffset Now = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeStaffStore _store = new();
    private StaffService Sut() => new(_store, new FakeClock(Now));
    private static Actor Owner() => Actor.ForTest(Roles.Owner, null, Guid.NewGuid(), Guid.NewGuid());

    private static UpdateSpecialistRequest Upd(int? year = null, string? bio = null, string? photo = null) =>
        new("Master", null, null, null, year, bio, photo);

    [Theory]
    [InlineData("http://cdn.example.com/a.jpg")]
    [InlineData("ftp://cdn.example.com/a.jpg")]
    [InlineData("//cdn.example.com/a.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/a.jpg")]
    [InlineData("https://user:pass@cdn.example.com/a.jpg")]
    [InlineData("https://")]
    public async Task update_rejects_non_https_photo_url(string url)
    {
        var sp = _store.AddSpecialist();
        var r = await Sut().UpdateAsync(Owner(), sp.Id, Upd(photo: url), default);
        Assert.Equal("invalid_photo_url", r.Error!.Code);
        Assert.Null(_store.Specialists.Single().PhotoUrl);
    }

    [Fact]
    public async Task photo_url_longer_than_500_is_rejected_and_https_up_to_500_is_accepted()
    {
        var sp = _store.AddSpecialist();
        var prefix = "https://cdn.example.com/";
        var ok = prefix + new string('a', 500 - prefix.Length);
        var tooLong = ok + "a";
        Assert.Equal("invalid_photo_url", (await Sut().UpdateAsync(Owner(), sp.Id, Upd(photo: tooLong), default)).Error!.Code);
        var saved = await Sut().UpdateAsync(Owner(), sp.Id, Upd(photo: ok), default);
        Assert.Equal(ok, saved.Value!.PhotoUrl);
    }

    [Fact]
    public async Task create_validates_photo_year_and_bio()
    {
        Task<Result<SpecialistDto>> Create(int? year = null, string? bio = null, string? photo = null) =>
            Sut().CreateAsync(Owner(), new CreateSpecialistRequest("Anna", null, null, null, null, null, year, bio, photo), default);

        Assert.Equal("invalid_photo_url", (await Create(photo: "http://x.test/a.jpg")).Error!.Code);
        Assert.Equal("invalid_practice_start_year", (await Create(year: 1949)).Error!.Code);
        Assert.Equal("invalid_practice_start_year", (await Create(year: 2031)).Error!.Code); // пізніше за поточний рік
        Assert.Equal("invalid_practice_start_year", (await Create(year: 0)).Error!.Code);    // «очистити» лише в update
        Assert.Equal("invalid_bio", (await Create(bio: new string('x', 501))).Error!.Code);

        var ok = await Create(year: 2030, bio: new string('x', 500), photo: "https://cdn.example.com/a.jpg");
        Assert.True(ok.IsOk);
        Assert.Equal(2030, ok.Value!.PracticeStartYear);
        Assert.Equal(500, ok.Value.Bio!.Length);
        Assert.Equal("https://cdn.example.com/a.jpg", ok.Value.PhotoUrl);
    }

    [Fact]
    public async Task update_with_nulls_keeps_the_profile_and_empty_values_clear_it()
    {
        var sp = _store.AddSpecialist();
        var set = await Sut().UpdateAsync(Owner(), sp.Id, Upd(2015, "Colorist", "https://cdn.example.com/a.jpg"), default);
        Assert.Equal((2015, "Colorist", "https://cdn.example.com/a.jpg"), (set.Value!.PracticeStartYear, set.Value.Bio, set.Value.PhotoUrl));

        var kept = await Sut().UpdateAsync(Owner(), sp.Id, Upd(), default); // старий клієнт без нових полів
        Assert.Equal((2015, "Colorist", "https://cdn.example.com/a.jpg"), (kept.Value!.PracticeStartYear, kept.Value.Bio, kept.Value.PhotoUrl));

        var cleared = await Sut().UpdateAsync(Owner(), sp.Id, Upd(0, "", ""), default);
        Assert.Equal((null, null, null), (cleared.Value!.PracticeStartYear, cleared.Value.Bio, cleared.Value.PhotoUrl));
    }

    [Fact]
    public async Task update_validates_year_range()
    {
        var sp = _store.AddSpecialist();
        Assert.Equal("invalid_practice_start_year", (await Sut().UpdateAsync(Owner(), sp.Id, Upd(year: 1949), default)).Error!.Code);
        Assert.Equal("invalid_practice_start_year", (await Sut().UpdateAsync(Owner(), sp.Id, Upd(year: 2031), default)).Error!.Code);
        Assert.True((await Sut().UpdateAsync(Owner(), sp.Id, Upd(year: 1950), default)).IsOk);
    }
}
