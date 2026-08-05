namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Invoice3Service
{
    private readonly Invoice3Repository repository;

    public Invoice3Service(Invoice3Repository repository) => this.repository = repository;

    public Invoice3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
