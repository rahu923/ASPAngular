using System.Data;
using OrderManagement.Domain;
namespace OrderManagement.Application;

public class OrderService
{
    // Concept: an order is one business aggregate. This service coordinates parent,
    // items and inventory; controllers/repositories do not independently commit parts of it.
    private readonly IOrderRepository orders;
    private readonly IProductRepository products;
    private readonly IUnitOfWork work;
    public OrderService(IOrderRepository orders, IProductRepository products, IUnitOfWork work)
        => (this.orders, this.products, this.work) = (orders, products, work);
    public Task<Page<OrderDto>> ListAsync(string userId, bool all, OrderQuery query, CancellationToken ct) => orders.ListAsync(userId, all, query, ct);
    public async Task<OrderDto> ReadAsync(int id, CancellationToken ct) => await orders.ReadAsync(id, ct) ?? throw new BusinessException("Order not found.", 404);

    public async Task<OrderDto> CreateAsync(string customerId, CreateOrderRequest input, CancellationToken ct)
    {
        if (input.Items.Count == 0 || input.Items.Any(x => x.Quantity <= 0) || input.Items.GroupBy(x => x.ProductId).Any(x => x.Count() > 1))
            throw new BusinessException("Supply distinct products with positive quantities.");
        // Serializable protects the read/write assumptions for stock reservation.
        // Keep this transaction short: database work only, no email or external HTTP calls.
        // If any product fails validation, await using rolls back the whole unit of work.
        await using var tx = await work.BeginAsync(IsolationLevel.Serializable, ct);
        var order = new Order { CustomerId = customerId };
        // Stable order of stock locks reduces deadlocks. Rowversion and SQL constraints remain the final guard.
        foreach (var item in input.Items.OrderBy(x => x.ProductId))
        {
            var product = await ReserveAsync(item.ProductId, item.Quantity, ct);
            // Snapshot the SERVER price once; later product price changes must not rewrite history.
            order.Items.Add(new OrderItem { ProductId = product.Id, Quantity = item.Quantity, UnitPrice = product.Price });
        }
        order.TotalAmount = order.Items.Sum(x => x.Quantity * x.UnitPrice);
        orders.Add(order);
        // Save flushes tracked order/items/stock together, but the explicit transaction
        // is still uncommitted until CommitAsync succeeds. SQL also generates the order ID here.
        await work.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ReadAsync(order.Id, ct);
    }
    public async Task<OrderDto> ChangeItemAsync(int id, string userId, bool admin, string version, int? itemId, int? productId, int? quantity, CancellationToken ct)
    {
        // The controller maps add/update/remove to this operation. A missing quantity
        // means removal only on the delete path; request DTOs reject zero for normal edits.
        await using var tx = await work.BeginAsync(IsolationLevel.Serializable, ct);
        var order = await LoadAsync(id, userId, admin, version, ct);
        if (order.Status != OrderStatus.Draft) throw new BusinessException("Only Draft orders allow item changes.", 409);
        if (itemId is null)
        {
            if (order.Items.Count >= 100 || order.Items.Any(x => x.ProductId == productId)) throw new BusinessException("Product already added or order item limit reached.");
            var product = await ReserveAsync(productId!.Value, quantity!.Value, ct);
            order.Items.Add(new OrderItem { ProductId = product.Id, Quantity = quantity.Value, UnitPrice = product.Price });
        }
        else
        {
            var item = order.Items.SingleOrDefault(x => x.Id == itemId) ?? throw new BusinessException("Item not found.", 404);
            var product = await products.FindForUpdateAsync(item.ProductId, ct) ?? throw new BusinessException("Product not found.", 404);
            var next = quantity ?? 0;
            if (next < 0) throw new BusinessException("Quantity must be positive.");
            // Reserve only the difference: 2 -> 5 consumes 3; 5 -> 2 returns 3.
            // This keeps inventory consistent without releasing/re-reserving the entire line.
            var delta = next - item.Quantity;
            if (delta > 0 && (!product.IsActive || product.StockQuantity < delta)) throw new BusinessException("Product unavailable or insufficient stock.", 409);
            product.StockQuantity -= delta;
            if (next == 0)
            {
                if (order.Items.Count == 1) throw new BusinessException("Cancel the order instead of removing its last item.", 409);
                // Mark deletion in EF AND update the in-memory aggregate used to recalculate totals.
                orders.RemoveItem(item);
                order.Items.Remove(item);
            }
            else item.Quantity = next; // Keep the original unit-price snapshot.
        }
        order.TotalAmount = order.Items.Sum(x => x.Quantity * x.UnitPrice);
        await work.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ReadAsync(id, ct);
    }
    public async Task<OrderDto> TransitionAsync(int id, string userId, bool staff, string version, OrderStatus next, CancellationToken ct)
    {
        await using var tx = await work.BeginAsync(IsolationLevel.Serializable, ct);
        var order = await orders.FindAsync(id, ct) ?? throw new BusinessException("Order not found.", 404);
        if (!staff && order.CustomerId != userId) throw new BusinessException("Order not found.", 404);
        // The locked parent makes cancellation idempotent. Return before checking the
        // client's old version: a repeated cancellation must not add stock a second time.
        if (order.Status == OrderStatus.Cancelled && next == OrderStatus.Cancelled) return await ReadAsync(id, ct);
        CheckVersion(order, version);
        // Explicit transition graph: do not let clients assign arbitrary enum values.
        // Owners can cancel eligible orders; staff alone can advance fulfillment.
        var valid = next == OrderStatus.Cancelled ? order.Status is OrderStatus.Draft or OrderStatus.Confirmed
            : staff && ((order.Status == OrderStatus.Draft && next == OrderStatus.Confirmed)
                || (order.Status == OrderStatus.Confirmed && next == OrderStatus.Shipped)
                || (order.Status == OrderStatus.Shipped && next == OrderStatus.Completed));
        if (!valid) throw new BusinessException("Invalid order status transition.", 409);
        if (next == OrderStatus.Cancelled)
            foreach (var item in order.Items.OrderBy(x => x.ProductId))
            {
                var product = await products.FindForUpdateAsync(item.ProductId, ct) ?? throw new BusinessException("Product missing.", 409);
                product.StockQuantity += item.Quantity;
            }
        order.Status = next;
        await work.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ReadAsync(id, ct);
    }
    private async Task<Product> ReserveAsync(int id, int quantity, CancellationToken ct)
    {
        if (quantity <= 0) throw new BusinessException("Quantity must be positive.");
        var product = await products.FindForUpdateAsync(id, ct) ?? throw new BusinessException("Product not found.", 404);
        if (!product.IsActive || product.StockQuantity < quantity) throw new BusinessException("Product unavailable or insufficient stock.", 409);
        // This changes a tracked object only. UoW SaveChanges + the outer commit persist it.
        product.StockQuantity -= quantity;
        return product;
    }
    private async Task<Order> LoadAsync(int id, string userId, bool admin, string version, CancellationToken ct)
    {
        var order = await orders.FindAsync(id, ct) ?? throw new BusinessException("Order not found.", 404);
        // Recheck ownership inside the mutation service; a UI guard or prior read is not authority.
        // Use 404 so another customer's order existence is not disclosed.
        if (!admin && order.CustomerId != userId) throw new BusinessException("Order not found.", 404);
        CheckVersion(order, version);
        return order;
    }
    private static void CheckVersion(Order order, string version)
    {
        if (Convert.ToBase64String(order.RowVersion) != version) throw new BusinessException("Order changed. Reload before editing.", 409);
    }
}
