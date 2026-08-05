namespace Scale.Repositories;

using Scale.Domain;

public sealed class Account3Repository
{
    private readonly Dictionary<int, Account3Record> items = new();

    public Account3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Account3Record> All() => this.items.Values;

    public void Upsert(Account3Record record) => this.items[record.Id] = record;
}
