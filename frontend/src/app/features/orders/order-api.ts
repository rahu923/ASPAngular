import { Injectable, inject } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { Page } from "../products/product-api";
// Match the API's string-enum JSON contract. This narrows TypeScript callers; server
// validation is still required because TypeScript types do not validate network payloads.
export type OrderStatus =
  | "Draft"
  | "Confirmed"
  | "Shipped"
  | "Completed"
  | "Cancelled";
export interface OrderItem {
  id: number;
  productId: number;
  productName: string;
  quantity: number;
  unitPrice: number;
}
export interface Order {
  id: number;
  customerId: string;
  createdAtUtc: string;
  status: OrderStatus;
  totalAmount: number;
  rowVersion: string;
  items: OrderItem[];
}
@Injectable({ providedIn: "root" })
export class OrderApi {
  private readonly http = inject(HttpClient);
  list(pageNumber: number, search: string, status: string, sort: string) {
    const params: Record<string, string | number> = {
      pageNumber,
      pageSize: 12,
      search,
      sort,
    };
    // Omit the status parameter when the user selects "All".
    if (status) {
      params["status"] = status;
    }
    return this.http.get<Page<Order>>("/api/orders", {
      params,
    });
  }
  get(id: number) {
    return this.http.get<Order>(`/api/orders/${id}`);
  }
  create(items: { productId: number; quantity: number }[]) {
    return this.http.post<Order>("/api/orders", { items });
  }
  add(order: Order, productId: number, quantity: number) {
    // The parent rowversion coordinates ALL item changes, not an independent per-item version.
    return this.http.post<Order>(`/api/orders/${order.id}/items`, {
      productId,
      quantity,
      rowVersion: order.rowVersion,
    });
  }
  update(order: Order, itemId: number, quantity: number) {
    return this.http.put<Order>(`/api/orders/${order.id}/items/${itemId}`, {
      quantity,
      rowVersion: order.rowVersion,
    });
  }
  remove(order: Order, itemId: number) {
    // HttpClient encodes query parameters (including '+'/'/' in Base64) for us.
    return this.http.delete<Order>(`/api/orders/${order.id}/items/${itemId}`, {
      params: { rowVersion: order.rowVersion },
    });
  }
  cancel(order: Order) {
    return this.http.post<Order>(`/api/orders/${order.id}/cancel`, {
      rowVersion: order.rowVersion,
    });
  }
  status(order: Order, status: string) {
    return this.http.put<Order>(`/api/orders/${order.id}/status`, {
      status,
      rowVersion: order.rowVersion,
    });
  }
}
