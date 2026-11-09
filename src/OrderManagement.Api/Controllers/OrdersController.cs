using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderManagement.Api.Security;
using OrderManagement.Application;
using OrderManagement.Domain;
namespace OrderManagement.Api.Controllers;
[ApiController, Route("api/orders"), Authorize]
public class OrdersController : ControllerBase
{
    private readonly OrderService orders;
    private readonly IAuthorizationService authorization;
    private readonly CatalogCache catalog;
    public OrdersController(OrderService orders, IAuthorizationService authorization, CatalogCache catalog) => (this.orders, this.authorization, this.catalog) = (orders, authorization, catalog);
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private bool Staff => User.HasClaim("permission", "ManageOrderStatus");
    [HttpGet]
    public Task<Page<OrderDto>> List([FromQuery] OrderQuery query, CancellationToken ct) => orders.ListAsync(UserId, User.HasClaim("permission", "ViewAllOrders"), query, ct);
    private async Task<OrderDto> Accessible(int id, bool edit, CancellationToken ct)
    {
        // Resource-based authorization evaluates this order's CustomerId, not merely a role.
        // A nested item URL must pass the parent-order check before it can expose/change an item.
        var order = await orders.ReadAsync(id, ct);
        if (!(await authorization.AuthorizeAsync(User, order, new OrderAccessRequirement(edit))).Succeeded) throw new BusinessException("Order not found.", 404);
        return order;
    }
    [HttpGet("{id:int}")]
    public async Task<OrderDto> Get(int id, CancellationToken ct) => await Accessible(id, false, ct);
    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderRequest input, CancellationToken ct)
    {
        var order = await orders.CreateAsync(UserId, input, ct); catalog.Invalidate();
        return CreatedAtAction(nameof(Get), new { id = order.Id }, order);
    }
    [HttpGet("{id:int}/items")]
    public async Task<List<OrderItemDto>> Items(int id, CancellationToken ct) => (await Accessible(id, false, ct)).Items;
    [HttpPost("{id:int}/items")]
    public async Task<OrderDto> Add(int id, AddItemRequest input, CancellationToken ct)
    {
        await Accessible(id, true, ct);
        var order = await orders.ChangeItemAsync(id, UserId, User.IsInRole("Admin"), input.RowVersion, null, input.ProductId, input.Quantity, ct);
        catalog.Invalidate(); return order;
    }
    [HttpPut("{id:int}/items/{itemId:int}")]
    public async Task<OrderDto> Update(int id, int itemId, EditItemRequest input, CancellationToken ct)
    {
        await Accessible(id, true, ct);
        var order = await orders.ChangeItemAsync(id, UserId, User.IsInRole("Admin"), input.RowVersion, itemId, null, input.Quantity, ct);
        catalog.Invalidate(); return order;
    }
    [HttpDelete("{id:int}/items/{itemId:int}")]
    public async Task<OrderDto> Remove(int id, int itemId, [FromQuery] string rowVersion, CancellationToken ct)
    {
        await Accessible(id, true, ct);
        var order = await orders.ChangeItemAsync(id, UserId, User.IsInRole("Admin"), rowVersion, itemId, null, null, ct);
        catalog.Invalidate(); return order;
    }
    [HttpPost("{id:int}/cancel")]
    public async Task<OrderDto> Cancel(int id, VersionRequest input, CancellationToken ct)
    {
        var order = await orders.TransitionAsync(id, UserId, Staff, input.RowVersion, OrderStatus.Cancelled, ct); catalog.Invalidate(); return order;
    }
    [HttpPut("{id:int}/status"), Authorize(Policy = "ManageOrderStatus")]
    // Policy grants the operation; the service still checks the actual allowed transition/version.
    public async Task<OrderDto> Status(int id, StatusRequest input, CancellationToken ct)
    {
        var order = await orders.TransitionAsync(id, UserId, true, input.RowVersion, input.Status, ct); catalog.Invalidate(); return order;
    }
}
