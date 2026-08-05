namespace Scale.Repositories;

using Scale.Domain;

public sealed class Account2Repository
{
    private readonly Dictionary<int, Account2Record> items = new();

    public Account2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Account2Record> All() => this.items.Values;

    public void Upsert(Account2Record record) => this.items[record.Id] = record;
}
