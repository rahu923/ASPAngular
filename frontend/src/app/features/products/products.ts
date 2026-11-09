import { Component, OnDestroy, OnInit, computed, inject, signal } from "@angular/core";
import { CurrencyPipe } from "@angular/common";
import { FormControl, ReactiveFormsModule } from "@angular/forms";
import { RouterLink } from "@angular/router";
import { Subscription, debounceTime, distinctUntilChanged } from "rxjs";
import { Page, Product, ProductApi } from "./product-api";

@Component({
  standalone: true,
  imports: [ReactiveFormsModule, CurrencyPipe, RouterLink],
  templateUrl: "./products.component.html",
})
export class Products implements OnInit, OnDestroy {
  private readonly api = inject(ProductApi);
  private readonly subscriptions = new Subscription();
  private productRequest?: Subscription;

  // Form controls hold what the user types or selects.
  readonly search = new FormControl("", { nonNullable: true });
  readonly sort = new FormControl("name", { nonNullable: true });

  // Signals hold values displayed in the HTML. Read with (), update with .set().
  readonly data = signal<Page<Product>>({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 12,
  });
  readonly loading = signal(true);
  readonly error = signal(false);
  readonly page = signal(1);
  readonly pages = computed(() =>
    Math.max(1, Math.ceil(this.data().totalCount / this.data().pageSize)),
  );

  ngOnInit() {
    this.loadProducts();

    // Wait until the user pauses typing before searching.
    this.subscriptions.add(
      this.search.valueChanges
        .pipe(debounceTime(300), distinctUntilChanged())
        .subscribe(() => this.applyFilters()),
    );
    this.subscriptions.add(
      this.sort.valueChanges.subscribe(() => this.applyFilters()),
    );
  }

  applyFilters() {
    this.page.set(1);
    this.loadProducts();
  }

  loadProducts() {
    // Cancel the old request so an old response cannot replace newer results.
    this.productRequest?.unsubscribe();
    this.loading.set(true);
    this.error.set(false);

    this.productRequest = this.api
      .list(this.search.value, this.sort.value, this.page())
      .subscribe({
        next: (result) => {
          this.data.set(result);
          this.loading.set(false);
        },
        error: () => {
          this.error.set(true);
          this.loading.set(false);
        },
      });
  }

  move(delta: number) {
    const nextPage = this.page() + delta;
    if (nextPage < 1 || nextPage > this.pages()) return;

    this.page.set(nextPage);
    this.loadProducts();
  }

  ngOnDestroy() {
    // Clean up listeners and the active request when leaving this page.
    this.subscriptions.unsubscribe();
    this.productRequest?.unsubscribe();
  }
}
