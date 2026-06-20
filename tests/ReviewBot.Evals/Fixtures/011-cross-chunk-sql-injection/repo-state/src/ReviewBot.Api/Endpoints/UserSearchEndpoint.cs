using Microsoft.AspNetCore.Mvc;
using ReviewBot.Persistence.Users;

namespace ReviewBot.Api.Endpoints;

public static class UserSearchEndpoint
{
    public static IEndpointRouteBuilder MapUserSearch(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/users/search", async (
            [FromQuery] string name,
            UserRepository repo,
            CancellationToken ct) =>
        {
            var user = await repo.FindByNameAsync(name, ct);
            return user is null ? Results.NotFound() : Results.Ok(user);
        });
        return routes;
    }
}
