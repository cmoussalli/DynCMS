using CMouss.IdentityFramework;
using DynCMS.Core.Data;
using DynCMS.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Security;

/// <summary>
/// Configures CMouss.IdentityFramework for DynCMS and adapts its static services to <see cref="ICmsIdentity"/>.
/// Registered as a singleton: the identity framework itself is process-wide.
/// </summary>
public sealed class CmsIdentity : ICmsIdentity
{
    private readonly StartupContext _startup;
    private readonly DynCmsIdentityOptions _options;
    private readonly DatabaseConfigurationStore _store;
    private readonly ILogger<CmsIdentity> _logger;
    private readonly object _initLock = new();

    // IDFManager's services share one EF DbContext, which is not thread-safe. Concurrent requests (the page, the
    // circuit, API calls) validating tokens at the same moment made it throw "A second operation was started on
    // this context instance", which ValidateToken turned into "not signed in" (an endless redirect to the login page,
    // more likely the slower the database). Every call into the framework's services goes through this lock.
    private static readonly object _frameworkLock = new();
    private bool _initialized;

    public CmsIdentity(IOptions<DynCmsOptions> options, DatabaseConfigurationStore store, StartupContext startup, ILogger<CmsIdentity> logger)
    {
        _startup = startup;
        _options = options.Value.Identity;
        _store = store;
        _logger = logger;
    }

    public TimeSpan TokenLifetime => TimeSpan.FromDays(Math.Max(1, _options.TokenLifetimeDays));

    /// <summary>
    /// Configures the identity framework against the configured database (its tables share the database with
    /// the content tables), creates its schema and master data (administrator account and role) and registers
    /// the DynCMS roles, entities and actions. Safe to call more than once.
    /// </summary>
    public void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;

            var config = _store.Current ?? throw new DynCmsNotConfiguredException();

            if (_options.TokenEncryptionKey == new DynCmsIdentityOptions().TokenEncryptionKey)
                _logger.LogWarning("DynCms:Identity:TokenEncryptionKey still has its default value. Set a unique key per environment.");

            IDFManager.Configure(new IDFManagerConfig
            {
                DatabaseType = config.Provider == DatabaseProvider.SqlServer ? DatabaseType.MSSQL : DatabaseType.SQLite,
                DBConnectionString = config.BuildConnectionString(_store.BasePath),
                DBLifeCycle = DBLifeCycle.Both,
                DefaultListPageSize = 25,
                IsActiveByDefault = true,
                IsLockedByDefault = false,
                DefaultTokenLifeTime = new LifeTime(Math.Max(1, _options.TokenLifetimeDays), 0, 0),
                AllowUserMultipleSessions = _options.AllowMultipleSessions,
                TokenEncryptionKey = _options.TokenEncryptionKey,
                TokenValidationMode = TokenValidationMode.DecryptAndValidate,
                AuthenticationBackend = AuthenticationBackend.Database,
                AdministratorUserName = _startup.RequestedAdmin?.UserName ?? _options.AdminUserName,
                AdministratorPassword = _startup.RequestedAdmin?.Password ?? _options.AdminPassword,
                AdministratorRoleId = CmsRoles.Admin,
                AdministratorRoleName = CmsRoles.Admin
            });

            using (var db = new IDFDBContext())
            {
                // Not EnsureCreated: the content tables already live in this database, which would make it a no-op.
                var created = DatabaseSchema.EnsureTables(db);
                db.InsertMasterData();
                _logger.LogInformation("DynCMS identity schema on {Database} {State}", config.Describe(_store.BasePath), created ? "created" : "ready");
            }
            IDFManager.RefreshIDFStorage();

            EnsureRole(CmsRoles.Editor, CmsRoles.Editor);
            foreach (var (id, title) in CmsPermissions.Entities.All) EnsureEntity(id, title);
            foreach (var (id, title) in CmsPermissions.Actions.All) EnsureAction(id, title);

            // Editors may work with content, media and the dictionary; administrators are authorised by role.
            foreach (var action in CmsPermissions.Actions.All)
            {
                EnsurePermission(CmsRoles.Editor, CmsPermissions.Entities.Content, action.Id);
                EnsurePermission(CmsRoles.Editor, CmsPermissions.Entities.Media, action.Id);
                EnsurePermission(CmsRoles.Editor, CmsPermissions.Entities.Dictionary, action.Id);
            }
            EnsurePermission(CmsRoles.Editor, CmsPermissions.Entities.DocumentTypes, CmsPermissions.Actions.Read);
            EnsurePermission(CmsRoles.Editor, CmsPermissions.Entities.Templates, CmsPermissions.Actions.Read);

            _initialized = true;
        }
    }

    /// <summary>
    /// Forgets that the framework was configured so the next <see cref="Initialize"/> points it at whatever
    /// database is configured then. Used when the database configuration is switched or reset from the back office.
    /// </summary>
    internal void Reset()
    {
        lock (_initLock) _initialized = false;
    }

    /// <summary>Reloads the framework's cached users, roles and permissions (after a database restore).</summary>
    internal void RefreshCache()
    {
        lock (_initLock)
        {
            if (_initialized) IDFManager.RefreshIDFStorage();
        }
    }

    public CmsSignInResult SignIn(string userName, string password, string? ipAddress = null)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return CmsSignInResult.Failed("Username and password are required.");

        try
        {
            var result = Locked(() => IDFManager.authService.AuthUserLogin(userName.Trim(), password, ipAddress));
            if (result.SecurityValidationResult != SecurityValidationResult.Ok || result.UserToken is null)
            {
                return CmsSignInResult.Failed(result.SecurityValidationResult switch
                {
                    SecurityValidationResult.IncorrectCredentials => "Invalid username or password.",
                    SecurityValidationResult.UnAuthorized => "This account is locked or inactive.",
                    _ => "Sign-in failed (" + result.SecurityValidationResult + ")."
                });
            }
            return new CmsSignInResult(true, result.UserToken.Token, result.UserToken.ExpireDate, null);
        }
        catch (Exception ex) when (ex is IncorrectPasswordException or UserNotFoundException or UnauthorizedException)
        {
            return CmsSignInResult.Failed("Invalid username or password.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-in failed for {User}", userName);
            return CmsSignInResult.Failed("Sign-in failed. Please try again.");
        }
    }

    public CmsPrincipal? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length <= 10) return null;
        try
        {
            // The framework decides whether the token is valid (decryption, expiry, user state,
            // sessions). The decrypted User object only carries identity data and roles.
            lock (_frameworkLock)
            {
                var auth = IDFManager.authService.AuthUserToken(token, TokenValidationMode.UseDefault);
                if (auth.SecurityValidationResult != SecurityValidationResult.Ok || auth.UserToken?.User is null)
                    return null;

                var userToken = auth.UserToken;
                var user = userToken.User;
                var roles = user.Roles?.Select(r => r.Id).ToList()
                            ?? IDFManager.userService.GetRoles(user.Id).Select(r => r.Id).ToList();
                var expires = userToken.ExpireDate > DateTime.MinValue.AddYears(1)
                    ? userToken.ExpireDate
                    : DateTime.UtcNow.Add(TokenLifetime);

                return new CmsPrincipal(user.Id, user.UserName, user.FullName, user.Email, roles, expires);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Token validation failed");
            return null;
        }
    }

    private static T Locked<T>(Func<T> call)
    {
        lock (_frameworkLock) return call();
    }

    public int CountUsers() => Locked(() => IDFManager.userService.GetAll(includeDeleted: false).Count);

    public IReadOnlyList<CmsUserSummary> GetUsers() =>
        Locked(() => IDFManager.userService.GetAll(includeDeleted: false).Select(Map).ToList());

    public CmsUserSummary? FindUserByName(string userName)
    {
        var user = IDFManager.userService.GetAll(includeDeleted: false)
            .FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.Ordinal));
        return user is null ? null : Map(user);
    }

    public CmsUserSummary? GetUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var user = IDFManager.userService.GetAll(includeDeleted: false)
            .FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.Ordinal));
        return user is null ? null : Map(user);
    }

    public IReadOnlyList<string> GetRoles() => IDFManager.roleService.GetAll().Select(r => r.Id).OrderBy(r => r).ToList();

    public string CreateUser(string userName, string password, string fullName, string email, params string[] roleIds)
    {
        var id = IDFManager.userService.Create(userName, password, fullName, email);
        foreach (var role in roleIds)
        {
            try { IDFManager.userService.GrantRole(id, role); }
            catch (AlreadyExistException) { }
        }
        return id;
    }

    public void EnsureRole(string roleId, string title)
    {
        if (IDFManager.roleService.GetAll().Any(r => string.Equals(r.Id, roleId, StringComparison.Ordinal))) return;
        try { IDFManager.roleService.Create(roleId, title); }
        catch (AlreadyExistException) { }
        IDFManager.RefreshIDFStorage();
    }

    public void EnsurePermission(string roleId, string entityId, string actionId)
    {
        var exists = IDFManager.permissionService.GetAll().Any(p =>
            string.Equals(p.RoleId, roleId, StringComparison.Ordinal) &&
            string.Equals(p.EntityId, entityId, StringComparison.Ordinal) &&
            string.Equals(p.PermissionTypeId, actionId, StringComparison.Ordinal));
        if (exists) return;
        try { IDFManager.permissionService.Create(CMouss.IdentityFramework.Helpers.GenerateId(), roleId, entityId, actionId); }
        catch (AlreadyExistException) { }
    }

    public bool UserHasRole(string userId, string roleId)
    {
        try { return IDFManager.userService.ValidateUserRole(userId, roleId); }
        catch { return false; }
    }

    public bool UserHasPermission(string userId, string entityId, string actionId)
    {
        try { return IDFManager.userService.ValidateUserPermission(userId, entityId, actionId); }
        catch { return false; }
    }

    private static void EnsureEntity(string id, string title)
    {
        if (IDFManager.entityService.GetAll().Any(e => string.Equals(e.Id, id, StringComparison.Ordinal))) return;
        try { IDFManager.entityService.Create(id, title); }
        catch (AlreadyExistException) { }
    }

    private static void EnsureAction(string id, string title)
    {
        if (IDFManager.permissionTypeService.GetAll().Any(e => string.Equals(e.Id, id, StringComparison.Ordinal))) return;
        try { IDFManager.permissionTypeService.Create(id, title); }
        catch (AlreadyExistException) { }
    }

    private static CmsUserSummary Map(User u) => new(
        u.Id, u.UserName, u.FullName, u.Email, u.IsActive, u.IsLocked,
        (u.Roles ?? IDFManager.userService.GetRoles(u.Id)).Select(r => r.Id).ToList());
}
