import { Component, OnDestroy, OnInit, inject, signal } from "@angular/core";
import { CurrencyPipe, DatePipe } from "@angular/common";
import { FormControl, ReactiveFormsModule } from "@angular/forms";
import { RouterLink } from "@angular/router";
import { Subscription, debounceTime, distinctUntilChanged } from "rxjs";
import { Auth } from "../../core/auth";
import { Feedback, Pager } from "../../shared/ui";
import { Page } from "../products/product-api";
import { Order, OrderApi } from "./order-api";
@Component({
  standalone: true,
  imports: [
    CurrencyPipe,
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    Feedback,
    Pager,
  ],
  templateUrl: "./orders.component.html",
})
export class Orders implements OnInit, OnDestroy {
  readonly auth = inject(Auth);
  private readonly api = inject(OrderApi);
  private readonly subscriptions = new Subscription();
  private orderRequest?: Subscription;
  readonly search = new FormControl("", { nonNullable: true });
  readonly status = new FormControl("", { nonNullable: true });
  readonly sort = new FormControl("newest", { nonNullable: true });
  readonly statuses = [
    "Draft",
    "Confirmed",
    "Shipped",
    "Completed",
    "Cancelled",
  ];
  readonly error = signal("");
  readonly loading = signal(true);
  readonly data = signal<Page<Order>>({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 12,
  });
  pages() {
    return Math.max(1, Math.ceil(this.data().totalCount / 12));
  }
  ngOnInit() {
    this.loadOrders();

    // Every filter change starts a new search on page 1.
    this.subscriptions.add(
      this.search.valueChanges
        .pipe(debounceTime(300), distinctUntilChanged())
        .subscribe(() => this.loadOrders()),
    );
    this.subscriptions.add(
      this.status.valueChanges.subscribe(() => this.loadOrders()),
    );
    this.subscriptions.add(
      this.sort.valueChanges.subscribe(() => this.loadOrders()),
    );
  }

  loadOrders(page = 1) {
    // Stop the previous request before starting a new one.
    this.orderRequest?.unsubscribe();
    this.loading.set(true);
    this.error.set("");

    this.orderRequest = this.api
      .list(page, this.search.value, this.status.value, this.sort.value)
      .subscribe({
        next: (data) => {
          this.data.set(data);
          this.loading.set(false);
        },
        error: (error) => {
          this.error.set(error.error?.title ?? "Could not load orders.");
          this.loading.set(false);
        },
      });
  }

  ngOnDestroy() {
    this.subscriptions.unsubscribe();
    this.orderRequest?.unsubscribe();
  }
}
