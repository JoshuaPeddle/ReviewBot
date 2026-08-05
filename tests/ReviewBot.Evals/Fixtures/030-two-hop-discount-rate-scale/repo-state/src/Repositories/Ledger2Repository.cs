namespace Scale.Repositories;

using Scale.Domain;

public sealed class Ledger2Repository
{
    private readonly Dictionary<int, Ledger2Record> items = new();

    public Ledger2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Ledger2Record> All() => this.items.Values;

    public void Upsert(Ledger2Record record) => this.items[record.Id] = record;
}
