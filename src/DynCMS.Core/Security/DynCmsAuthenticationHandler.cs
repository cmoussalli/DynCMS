using System.Security.Claims;
using System.Text.Encodings.Web;
using DynCMS.Core.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Security;

/// <summary>
/// ASP.NET Core authentication handler that turns a CMouss.IdentityFramework token (from the
/// <c>IDF_AuthToken</c> cookie written by the framework's Blazor UI, or an <c>Authorization: Bearer</c>
/// header) into a <see cref="ClaimsPrincipal"/>. This makes <c>[Authorize]</c>, <c>AuthorizeView</c> and
/// <c>AuthenticationStateProvider</c> work on top of the identity framework.
/// </summary>
public sealed class DynCmsAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICmsIdentity identity,
    IApiKeyService apiKeys,
    IOptions<DynCmsOptions> cmsOptions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // An API key in the Authorization header wins over the cookie, so a script run from a signed-in browser
        // (or an agent that happens to share the cookie jar) is still limited to the key's scopes.
        var bearer = ReadBearer();
        if (bearer is not null && bearer.StartsWith(apiKeys.SecretPrefix, StringComparison.Ordinal))
        {
            var key = await apiKeys.ValidateAsync(bearer, Context.RequestAborted);
            if (key is null) return AuthenticateResult.Fail("The API key is unknown, revoked or expired.");

            var keyClaims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, key.User.Id),
                new(ClaimTypes.Name, key.User.UserName),
                new(CmsClaimTypes.AuthMethod, CmsClaimTypes.ApiKeyMethod),
                new(CmsClaimTypes.ApiKeyId, key.Key.Id.ToString())
            };
            if (!string.IsNullOrWhiteSpace(key.User.FullName)) keyClaims.Add(new Claim(ClaimTypes.GivenName, key.User.FullName));
            if (!string.IsNullOrWhiteSpace(key.User.Email)) keyClaims.Add(new Claim(ClaimTypes.Email, key.User.Email));
            keyClaims.AddRange(key.User.RoleIds.Select(r => new Claim(ClaimTypes.Role, r)));
            keyClaims.AddRange(key.Key.Scopes.Select(s => new Claim(CmsClaimTypes.Scope, s)));

            var keyPrincipal = new ClaimsPrincipal(new ClaimsIdentity(keyClaims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role));
            var keyProps = new AuthenticationProperties { ExpiresUtc = key.Key.ExpiresAt?.ToUniversalTime() };
            return AuthenticateResult.Success(new AuthenticationTicket(keyPrincipal, keyProps, Scheme.Name));
        }

        var token = ReadCookie() ?? bearer;
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        var principalInfo = identity.ValidateToken(token);
        if (principalInfo is null && token.Contains('%'))
            principalInfo = identity.ValidateToken(Uri.UnescapeDataString(token));
        if (principalInfo is null) return AuthenticateResult.NoResult();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principalInfo.UserId),
            new(ClaimTypes.Name, principalInfo.UserName),
            new(CmsClaimTypes.AuthMethod, CmsClaimTypes.SessionMethod)
        };
        if (!string.IsNullOrWhiteSpace(principalInfo.FullName)) claims.Add(new Claim(ClaimTypes.GivenName, principalInfo.FullName));
        if (!string.IsNullOrWhiteSpace(principalInfo.Email)) claims.Add(new Claim(ClaimTypes.Email, principalInfo.Email));
        claims.AddRange(principalInfo.RoleIds.Select(r => new Claim(ClaimTypes.Role, r)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = principalInfo.TokenExpiresAt.ToUniversalTime() }, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (IsApiRequest())
        {
            // Machines get a proper 401; only browsers are sent to the login page.
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Bearer realm=\"DynCMS\"";
            return Response.WriteAsJsonAsync(
                new ProblemDto(401, "Unauthorized", "Send a DynCMS API key as 'Authorization: Bearer <key>'. Keys are created in the back office under Settings → API & AI agents.", null),
                ApiJson.Options, cancellationToken: Context.RequestAborted);
        }

        var returnUrl = Uri.EscapeDataString(Request.Path + Request.QueryString);
        Response.Redirect($"{cmsOptions.Value.Identity.LoginPath}?returnUrl={returnUrl}");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (!IsApiRequest()) return base.HandleForbiddenAsync(properties);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new ProblemDto(403, "Forbidden", "You are not allowed to do this.", null), ApiJson.Options, cancellationToken: Context.RequestAborted);
    }

    private bool IsApiRequest()
    {
        var api = cmsOptions.Value.Api;
        var path = Request.Path;
        if (path.StartsWithSegments("/" + api.BasePath.Trim('/')) || path.StartsWithSegments("/" + api.McpPath.Trim('/'))) return true;
        if (!string.IsNullOrEmpty(Request.Headers.Authorization)) return true;
        return Request.Headers["Sec-Fetch-Mode"] != "navigate" &&
               !Request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    }

    private string? ReadCookie() =>
        Request.Cookies.TryGetValue(cmsOptions.Value.Identity.TokenCookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie) ? cookie : null;

    private string? ReadBearer()
    {
        var auth = Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
    }
}
