using Microsoft.EntityFrameworkCore;
using OrderManagement.Application;
using OrderManagement.Domain;
namespace OrderManagement.Infrastructure;
public class ProductRepository : IProductRepository
{
    private readonly OrderDbContext db;
    public ProductRepository(OrderDbContext db) => this.db = db;
    public async Task<Page<ProductDto>> ListAsync(ProductQuery query, CancellationToken ct)
    {
        // Concept: IQueryable composes SQL until CountAsync/ToListAsync executes it.
        // AsNoTracking avoids tracking read entities; pure DTO projections do not need tracking either.
        var products = db.Products.AsNoTracking().Where(x => query.IncludeInactive || x.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
            products = products.Where(x => x.Name.Contains(query.Search) || x.Sku.Contains(query.Search));
        // Count before Skip/Take so the UI knows the full filtered result size.
        // Count and page are separate reads and may observe concurrent catalog changes.
        var count = await products.CountAsync(ct);
        // The ID tie-breaker gives deterministic ordering when names/prices are equal.
        var sorted = query.Sort switch
        {
            "price" => products.OrderBy(x => x.Price).ThenBy(x => x.Id),
            "price_desc" => products.OrderByDescending(x => x.Price).ThenBy(x => x.Id),
            _ => products.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
        // Project the needed columns before materializing; do not load a whole table and page in memory.
        // Async execution yields while SQL runs; ct propagates request cancellation to database I/O.
        var items = await sorted.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new ProductDto(x.Id, x.Name, x.Sku, x.Description, x.Price, x.StockQuantity, x.IsActive, Convert.ToBase64String(x.RowVersion))).ToListAsync(ct);
        return new(items, count, query.PageNumber, query.PageSize);
    }
    public Task<ProductDto?> GetAsync(int id, CancellationToken ct) => db.Products.AsNoTracking()
        .Where(x => x.Id == id && x.IsActive)
        .Select(x => new ProductDto(x.Id, x.Name, x.Sku, x.Description, x.Price, x.StockQuantity, x.IsActive, Convert.ToBase64String(x.RowVersion))).SingleOrDefaultAsync(ct);
    // Tracked entity for writes. FromSqlInterpolated parameterizes id; it is not string-built SQL.
    // Inside an explicit transaction, UPDLOCK holds the update lock until its end and
    // reduces competing readers converting shared locks to exclusive locks. ROWLOCK is a hint;
    // SQL Server may escalate. Outside that transaction this SELECT cannot protect a later save.
    public Task<Product?> FindForUpdateAsync(int id, CancellationToken ct) => db.Products
        .FromSqlInterpolated($"SELECT * FROM [Products] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);
    public void Add(Product product) => db.Products.Add(product);
}
public class UnitOfWork : IUnitOfWork
{
    // DI gives this UoW and every scoped repository the SAME context/change tracker.
    // SaveChanges includes all their tracked changes, not just the most recently used repository.
    private readonly OrderDbContext db;
    public UnitOfWork(OrderDbContext db) => this.db = db;
    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
    public async Task<IWorkTransaction> BeginAsync(System.Data.IsolationLevel isolation, CancellationToken ct)
        => new WorkTransaction(await db.Database.BeginTransactionAsync(isolation, ct));
}
internal class WorkTransaction : IWorkTransaction
{
    // Wrapping EF's transaction keeps EF types out of Application. await using ensures
    // disposal on success, exception or cancellation; disposal rolls back an uncommitted transaction.
    private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx;
    public WorkTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) => this.tx = tx;
    public Task CommitAsync(CancellationToken ct) => tx.CommitAsync(ct);
    public ValueTask DisposeAsync() => tx.DisposeAsync();
}
