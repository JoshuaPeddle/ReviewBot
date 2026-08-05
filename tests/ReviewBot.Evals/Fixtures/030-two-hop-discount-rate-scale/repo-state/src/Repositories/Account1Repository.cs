namespace Scale.Repositories;

using Scale.Domain;

public sealed class Account1Repository
{
    private readonly Dictionary<int, Account1Record> items = new();

    public Account1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Account1Record> All() => this.items.Values;

    public void Upsert(Account1Record record) => this.items[record.Id] = record;
}
