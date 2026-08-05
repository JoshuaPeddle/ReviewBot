#!/usr/bin/env python3
"""Regenerates this fixture's repo-state.

The corpus's other repo-states top out at 50 files, so retrieval and chunk planning are
never load-bearing in them: everything fits in the prompt and the right answer is always
already in context. This one is large enough that the reviewer must *find* the two files
that matter among ~300 similarly-shaped ones.

The planted defect needs two hops from the diff:
  CheckoutHandler (in the diff)  -> OrderService.ApplyDiscount  -> DiscountRepository.GetRate
The handler converts a rate to a percentage before passing it to a method that documents
its parameter as a fraction. Neither the bug nor the convention is visible in the diff.

Deterministic: same output every run, so regenerating never churns the diff.
"""
import os, pathlib

ROOT = pathlib.Path(__file__).parent / "repo-state"
FILLER_PER_LAYER = 72

DOMAINS = [
    "Account", "Address", "Basket", "Campaign", "Carrier", "Catalog", "Channel", "Customer",
    "Delivery", "Fulfilment", "Invoice", "Ledger", "Listing", "Manifest", "Merchant",
    "Notification", "Payment", "Pricing", "Refund", "Reservation", "Shipment", "Subscription",
    "Supplier", "Tariff", "Tax", "Tenant", "Voucher", "Warehouse",
]


def write(rel: str, body: str) -> None:
    path = ROOT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body.lstrip("\n"), encoding="utf-8")


def filler_name(index: int) -> str:
    return f"{DOMAINS[index % len(DOMAINS)]}{index // len(DOMAINS) + 1}"


def main() -> None:
    if ROOT.exists():
        for p in sorted(ROOT.rglob("*"), reverse=True):
            p.unlink() if p.is_file() else p.rmdir()

    # --- The two files the reviewer has to find -------------------------------------
    write("src/Repositories/DiscountRepository.cs", """
namespace Scale.Repositories;

/// <summary>Promotional discount lookup.</summary>
public sealed class DiscountRepository
{
    private readonly Dictionary<string, decimal> rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAVE10"] = 0.10m,
        ["SAVE15"] = 0.15m,
        ["HALFOFF"] = 0.50m,
    };

    /// <summary>
    /// Returns the discount as a <b>fraction</b> of the order total: 0.15 means 15% off.
    /// It is deliberately not a percentage, so callers never divide by 100.
    /// </summary>
    public decimal GetRate(string code) =>
        this.rates.TryGetValue(code, out var rate) ? rate : 0m;
}
""")

    write("src/Services/OrderService.cs", """
namespace Scale.Services;

using Scale.Repositories;

public sealed class OrderService
{
    private readonly DiscountRepository discounts;

    public OrderService(DiscountRepository discounts) => this.discounts = discounts;

    public decimal RateFor(string code) => this.discounts.GetRate(code);

    /// <summary>
    /// Subtracts <paramref name="rate"/> of <paramref name="total"/>.
    /// </summary>
    /// <param name="rate">
    /// A <b>fraction</b>, matching <see cref="DiscountRepository.GetRate"/> — 0.15 is 15% off.
    /// Passing 15 here discounts 1500% and returns a large negative total.
    /// </param>
    public decimal ApplyDiscount(decimal total, decimal rate) => total - (total * rate);
}
""")

    write("src/Handlers/CheckoutHandler.cs", """
namespace Scale.Handlers;

using Scale.Services;

public sealed class CheckoutHandler
{
    private readonly OrderService orders;

    public CheckoutHandler(OrderService orders) => this.orders = orders;

    public decimal Checkout(decimal total, string promoCode)
    {
        var rate = this.orders.RateFor(promoCode);
        var percent = rate * 100m;
        return this.orders.ApplyDiscount(total, percent);
    }
}
""")

    # --- Filler with the same shape, so the right files must actually be located -----
    for i in range(FILLER_PER_LAYER):
        n = filler_name(i)
        write(f"src/Domain/{n}Record.cs", f"""
namespace Scale.Domain;

public sealed record {n}Record(int Id, string Name, decimal Amount, DateTimeOffset UpdatedAt)
{{
    public bool IsActive => this.Amount > 0m;
}}
""")
        write(f"src/Repositories/{n}Repository.cs", f"""
namespace Scale.Repositories;

using Scale.Domain;

public sealed class {n}Repository
{{
    private readonly Dictionary<int, {n}Record> items = new();

    public {n}Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<{n}Record> All() => this.items.Values;

    public void Upsert({n}Record record) => this.items[record.Id] = record;
}}
""")
        write(f"src/Services/{n}Service.cs", f"""
namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class {n}Service
{{
    private readonly {n}Repository repository;

    public {n}Service({n}Repository repository) => this.repository = repository;

    public {n}Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}}
""")
        write(f"src/Handlers/{n}Handler.cs", f"""
namespace Scale.Handlers;

using Scale.Services;

public sealed class {n}Handler
{{
    private readonly {n}Service service;

    public {n}Handler({n}Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}}
""")

    count = sum(1 for _ in ROOT.rglob("*.cs"))
    print(f"generated {count} .cs files under {ROOT}")


if __name__ == "__main__":
    main()
