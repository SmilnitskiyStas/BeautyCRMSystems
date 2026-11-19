using BeautyCrm.Application.Features.BeautyPublicBooking;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Маскування контактів клієнта на сервері (§30.6): без <c>clients.contacts = full</c> повні <c>phone</c>/<c>email</c> у DTO відсутні,
/// натомість <c>phoneMasked</c> («+», потім • замість цифр, крім останніх двох) і <c>emailMasked</c> (перший символ локальної частини + «•••@» + домен).
/// </summary>
public static class ContactMasking
{
    public static bool CanSeeContacts(Actor actor) => actor.Access.AtLeast(PermissionKeys.ClientsContacts, PermissionLevels.Full);

    public static string? MaskPhone(string? phone) => PhoneNormalizer.Mask(phone);

    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "•••";
        return email[0] + "•••@" + email[(at + 1)..];
    }
}
