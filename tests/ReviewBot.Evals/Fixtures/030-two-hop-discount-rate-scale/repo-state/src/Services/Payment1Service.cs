namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Payment1Service
{
    private readonly Payment1Repository repository;

    public Payment1Service(Payment1Repository repository) => this.repository = repository;

    public Payment1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
