namespace OrderManagement.Domain;
// Enumerating states does not enforce transitions. OrderService explicitly validates
// the allowed graph and whether the current caller may perform the operation.
public enum OrderStatus { Draft, Confirmed, Shipped, Completed, Cancelled }
public class Order
{
    public int Id { get; set; }
    public string CustomerId { get; set; } = "";
    // Store UTC instants; convert to the viewer's local time only for presentation.
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public decimal TotalAmount { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    // Parent and items form the order aggregate; total/stock changes are coordinated by its service.
    public List<OrderItem> Items { get; set; } = new();
}
public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    // Historical price snapshot, not a reference to the product's current mutable price.
    public decimal UnitPrice { get; set; }
}
