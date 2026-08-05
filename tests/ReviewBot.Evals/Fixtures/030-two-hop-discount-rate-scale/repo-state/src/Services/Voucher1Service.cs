namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Voucher1Service
{
    private readonly Voucher1Repository repository;

    public Voucher1Service(Voucher1Repository repository) => this.repository = repository;

    public Voucher1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
