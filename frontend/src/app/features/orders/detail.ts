import { Component, DestroyRef, OnInit, computed, inject, signal } from "@angular/core";
import { CurrencyPipe } from "@angular/common";
import { ActivatedRoute } from "@angular/router";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Observable, Subscription } from "rxjs";
import { Auth } from "../../core/auth";
import { Feedback, Permission } from "../../shared/ui";
import { Order, OrderApi } from "./order-api";
import { Product, ProductApi } from "../products/product-api";
@Component({
  standalone: true,
  imports: [CurrencyPipe, Feedback, Permission],
  templateUrl: "./order-detail.component.html",
})
export class OrderDetail implements OnInit {
  private readonly api = inject(OrderApi);
  readonly auth = inject(Auth);
  private readonly catalog = inject(ProductApi);
  private readonly route = inject(ActivatedRoute);
  private readonly destroy = inject(DestroyRef);
  private productRequest?: Subscription;
  private orderRequest?: Subscription;
  readonly order = signal<Order | null>(null);
  readonly error = signal("");
  readonly success = signal("");
  readonly busy = signal(false);
  readonly products = signal<Product[]>([]);
  // Mirror the server rule for useful UI, not security. Backend ownership, state and
  // rowversion checks still run even if someone enables a hidden/disabled button manually.
  readonly canEdit = computed(() => {
    const order = this.order();
    if (!order || order.status !== "Draft") {
      return false;
    }
    return this.auth.isAdmin() || this.auth.user()?.id === order.customerId;
  });

  readonly nextStatus = computed(() => {
    switch (this.order()?.status) {
      case "Draft":
        return "Confirmed";
      case "Confirmed":
        return "Shipped";
      case "Shipped":
        return "Completed";
      default:
        return undefined;
    }
  });

  ngOnInit() {
    this.load();
    this.find("");
  }
  load() {
    this.orderRequest?.unsubscribe();
    this.error.set("");
    const orderId = Number(this.route.snapshot.paramMap.get("id"));
    this.orderRequest = this.api
      .get(orderId)
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (order) => this.order.set(order),
        error: (e) => this.error.set(e.error?.title ?? "Could not load order."),
      });
  }
  find(search: string) {
    this.productRequest?.unsubscribe();
    this.productRequest = this.catalog
      .list(search, "name", 1)
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (page) => this.products.set(page.items),
        error: () => this.error.set("Could not load products."),
      });
  }
  valid(quantity: number) {
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > 10000) {
      this.error.set("Quantity must be an integer between 1 and 10000.");
      return false;
    }
    return true;
  }
  add(productId: number, quantity: number) {
    const order = this.order();
    if (!order || !this.valid(quantity)) return;
    this.run(this.api.add(order, productId, quantity));
  }
  update(id: number, quantity: number) {
    const order = this.order();
    if (!order || !this.valid(quantity)) return;
    this.run(this.api.update(order, id, quantity));
  }

  remove(itemId: number) {
    const order = this.order();
    if (!order) return;
    this.run(this.api.remove(order, itemId));
  }

  cancel() {
    const order = this.order();
    if (!order) return;
    this.run(this.api.cancel(order));
  }

  changeStatus(status: string) {
    const order = this.order();
    if (!order) return;
    this.run(this.api.status(order, status));
  }

  private run(request: Observable<Order>) {
    // HttpClient has not sent this cold Observable yet. Subscribe once, then replace
    // the DTO with the response so the NEXT edit uses the newly generated parent rowversion.
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    this.success.set("");
    request.pipe(takeUntilDestroyed(this.destroy)).subscribe({
      next: (order) => {
        this.order.set(order);
        this.busy.set(false);
        this.success.set("Order updated.");
      },
      error: (e) => {
        this.busy.set(false);
        this.error.set(
          e.error?.title ?? "Update failed. Reload the order before retrying.",
        );
      },
    });
  }
}
