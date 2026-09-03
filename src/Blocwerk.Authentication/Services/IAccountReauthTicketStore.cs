namespace Blocwerk.Authentication.Services;

/// <summary>
/// Short-lived, single-use proof that a specific user completed a fresh OAuth sign-in with a
/// provider that account owns.
/// </summary>
/// <remarks>
/// The proof cannot live in the session cookie: the page that redeems it is an interactive Blazor
/// component, and a circuit has no <c>HttpContext</c> to read a cookie from or write one to. So the
/// OAuth callback stores the proof server-side and hands the page an opaque ticket id in the URL.
/// The ticket alone is worth nothing — redeeming it also requires being signed in as the user it was
/// issued to — and it is destroyed the moment the page redeems it, on arrival, so the copy left
/// behind in the address bar, in browser history or in a proxy log is already dead. There is
/// deliberately no way to test a ticket without spending it: that is what let a live ticket sit in
/// the URL of an unattended browser.
/// </remarks>
public interface IAccountReauthTicketStore
{
    /// <summary>Records a completed re-authentication and returns the ticket id to hand back.</summary>
    string Issue(Guid userId);

    /// <summary>Spends the ticket. False when it is unknown, expired or another user's.</summary>
    bool Consume(string? ticket, Guid userId);
}
