using Dapper;

namespace ReviewBot.Persistence.Users;

public sealed class UserRepository
{
    private readonly IDbConnectionFactory connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory;

    public async Task<User?> FindByNameAsync(string name, CancellationToken ct)
    {
        using var conn = connectionFactory.Create();
        var query = $"SELECT id, name, email FROM users WHERE name = '{name}'";
        return await conn.QuerySingleOrDefaultAsync<User>(query);
    }
}

public sealed record User(int Id, string Name, string Email);
