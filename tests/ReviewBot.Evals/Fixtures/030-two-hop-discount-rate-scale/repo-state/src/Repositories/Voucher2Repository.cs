namespace Scale.Repositories;

using Scale.Domain;

public sealed class Voucher2Repository
{
    private readonly Dictionary<int, Voucher2Record> items = new();

    public Voucher2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Voucher2Record> All() => this.items.Values;

    public void Upsert(Voucher2Record record) => this.items[record.Id] = record;
}
