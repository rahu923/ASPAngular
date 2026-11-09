using Microsoft.EntityFrameworkCore;
using OrderManagement.Application;
using OrderManagement.Domain;
namespace OrderManagement.Infrastructure;
public class OrderRepository : IOrderRepository
{
    private readonly OrderDbContext db;
    public OrderRepository(OrderDbContext db) => this.db = db;
    // Nested item projection avoids querying once per order/item (the N+1 problem).
    // SQL datetime2 does not retain DateTime.Kind; these values are stored as UTC,
    // so SpecifyKind labels them correctly for JSON rather than converting their clock value.
    private IQueryable<OrderDto> Project(IQueryable<Order> query) => query.Select(x => new OrderDto(x.Id, x.CustomerId, DateTime.SpecifyKind(x.CreatedAtUtc, DateTimeKind.Utc),
        x.Status, x.TotalAmount, Convert.ToBase64String(x.RowVersion), x.Items.OrderBy(i => i.Id)
            .Select(i => new OrderItemDto(i.Id, i.ProductId, i.Product.Name, i.Quantity, i.UnitPrice)).ToList()));
    public async Task<Page<OrderDto>> ListAsync(string userId, bool all, OrderQuery query, CancellationToken ct)
    {
        // Apply ownership BEFORE count/pagination. Filtering in the browser would leak data;
        // filtering after paging would also give incorrect counts and incomplete pages.
        var source = db.Orders.AsNoTracking().Where(x => all || x.CustomerId == userId);
        if (query.Status.HasValue) source = source.Where(x => x.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Search)) source = source.Where(x => x.Items.Any(i => i.Product.Name.Contains(query.Search)));
        var count = await source.CountAsync(ct);
        var sorted = query.Sort switch { "oldest" => source.OrderBy(x => x.Id), "total" => source.OrderByDescending(x => x.TotalAmount).ThenByDescending(x => x.Id), _ => source.OrderByDescending(x => x.Id) };
        return new(await Project(sorted.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)).ToListAsync(ct), count, query.PageNumber, query.PageSize);
    }
    public Task<OrderDto?> ReadAsync(int id, CancellationToken ct) => Project(db.Orders.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(ct);
    // Serialize mutations of the parent order (including two cancellations). Include loads
    // the tracked items needed to enforce aggregate rules inside the service's transaction.
    public Task<Order?> FindAsync(int id, CancellationToken ct) => db.Orders
        .FromSqlInterpolated($"SELECT * FROM [Orders] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").Include(x => x.Items).SingleOrDefaultAsync(ct);
    public void Add(Order order) => db.Orders.Add(order);
    public void RemoveItem(OrderItem item) => db.OrderItems.Remove(item);
}
