namespace Scale.Repositories;

using Scale.Domain;

public sealed class Ledger3Repository
{
    private readonly Dictionary<int, Ledger3Record> items = new();

    public Ledger3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Ledger3Record> All() => this.items.Values;

    public void Upsert(Ledger3Record record) => this.items[record.Id] = record;
}
