import { Component, DestroyRef, OnInit, computed, inject, signal } from "@angular/core";
import { CurrencyPipe } from "@angular/common";
import { FormControl, ReactiveFormsModule } from "@angular/forms";
import { Router } from "@angular/router";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Subscription, debounceTime, distinctUntilChanged } from "rxjs";
import { Feedback, Pager } from "../../shared/ui";
import { Product, ProductApi } from "../products/product-api";
import { OrderApi } from "./order-api";
interface CartLine {
  product: Product;
  quantity: number;
}
@Component({
  standalone: true,
  imports: [CurrencyPipe, ReactiveFormsModule, Feedback, Pager],
  templateUrl: "./create-order.component.html",
})
export class CreateOrder implements OnInit {
  private readonly api = inject(OrderApi);
  private readonly catalog = inject(ProductApi);
  private readonly router = inject(Router);
  private readonly destroy = inject(DestroyRef);
  private productRequest?: Subscription;
  readonly search = new FormControl("", { nonNullable: true });
  readonly products = signal<Product[]>([]);
  readonly cart = signal<CartLine[]>([]);
  // A derived UI preview recalculates when cart changes. This number is never trusted
  // for billing: create() sends only IDs/quantities and the server calculates the real total.
  readonly total = computed(() => {
    let total = 0;
    for (const line of this.cart()) {
      total += line.quantity * line.product.price;
    }
    return total;
  });
  readonly error = signal("");
  readonly busy = signal(false);
  readonly productPage = signal(1);
  readonly productPages = signal(1);
  ngOnInit() {
    this.loadPage(1);

    this.search.valueChanges
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe(() => this.loadPage(1));
  }

  loadPage(page: number) {
    this.productRequest?.unsubscribe();
    this.productPage.set(page);
    this.error.set("");

    this.productRequest = this.catalog
      .list(this.search.value, "name", page)
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (data) => {
          this.products.set(data.items);
          this.productPages.set(Math.max(1, Math.ceil(data.totalCount / data.pageSize)));
        },
        error: () => this.error.set("Could not load products."),
      });
  }

  add(product: Product) {
    const existing = this.cart().find((x) => x.product.id === product.id);
    if (existing) {
      this.quantity(product.id, existing.quantity + 1);
      return;
    }
    this.cart.update((lines) => [...lines, { product, quantity: 1 }]);
  }
  quantity(id: number, quantity: number, input?: HTMLInputElement) {
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > 10000) {
      if (input)
        input.value = String(
          this.cart().find((line) => line.product.id === id)?.quantity ?? 1,
        );
      this.error.set("Quantity must be an integer between 1 and 10000.");
      return;
    }
    this.error.set("");
    // Replace the array/changed line rather than mutating a nested object silently;
    // the signal's new reference tells computed state and templates to update.
    const updatedLines = this.cart().map((line) => {
      if (line.product.id === id) {
        return { ...line, quantity };
      }
      return line;
    });
    this.cart.set(updatedLines);
  }
  remove(id: number) {
    this.cart.update((lines) => lines.filter((x) => x.product.id !== id));
  }
  create() {
    // Busy avoids duplicate UI submissions; the API does not implement a creation idempotency key.
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    // Send IDs and quantities. The server calculates prices and the final total.
    const items = this.cart().map((line) => ({
      productId: line.product.id,
      quantity: line.quantity,
    }));
    this.api
      .create(items)
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (order) => void this.router.navigate(["/orders", order.id]),
        error: (e) => {
          this.busy.set(false);
          this.error.set(e.error?.title ?? "Order could not be created.");
        },
      });
  }
}
