namespace ReviewBot.Core.Sessions;

public static class SessionCacheKeys
{
    public static string ForUser(string userId) => $"session:user:{userId}";

    public static string ForTenantUser(string tenantId, string userId) => $"session:tenant:{tenantId}:user:{userId}";
}
