namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Voucher2Service
{
    private readonly Voucher2Repository repository;

    public Voucher2Service(Voucher2Repository repository) => this.repository = repository;

    public Voucher2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
