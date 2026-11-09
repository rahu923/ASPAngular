import { Component, DestroyRef, OnInit, inject, signal } from "@angular/core";
import { CurrencyPipe } from "@angular/common";
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from "@angular/forms";
import { HttpClient } from "@angular/common/http";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Observable, Subscription, debounceTime, distinctUntilChanged } from "rxjs";
import { Page, Product } from "../products/product-api";
import { Feedback, Pager } from "../../shared/ui";
@Component({
  // Standalone imports declare the directives/pipes/components this external template uses;
  // there is no feature NgModule collecting them for us.
  standalone: true,
  imports: [CurrencyPipe, ReactiveFormsModule, Feedback, Pager],
  templateUrl: "./product-admin.component.html",
})
export class ProductAdmin implements OnInit {
  // Angular supplies the HTTP service and the component's cleanup reference.
  private readonly http = inject(HttpClient);
  private readonly destroy = inject(DestroyRef);
  private listRequest?: Subscription;
  // nonNullable keeps reset()/value typed as string rather than string | null.
  readonly search = new FormControl("", { nonNullable: true });
  // Signals store local view state; call data() in HTML to read and register a reactive dependency.
  readonly data = signal<Page<Product>>({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 12,
  });
  readonly editing = signal<Product | null>(null);
  readonly error = signal("");
  readonly success = signal("");
  readonly busy = signal(false);
  // Typed controls define the editable request shape. Extra DTO fields (id, isActive,
  // rowVersion) are intentionally not editable controls. The API validates independently.
  readonly form = new FormGroup({
    name: new FormControl("", {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.minLength(2),
        Validators.maxLength(160),
      ],
    }),
    sku: new FormControl("", {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.minLength(2),
        Validators.pattern(/^[A-Z0-9-]+$/),
      ],
    }),
    description: new FormControl("", {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(2000)],
    }),
    price: new FormControl(1, {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.min(0.01),
        Validators.max(999999999.99),
      ],
    }),
    stockQuantity: new FormControl(0, {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.min(0),
        Validators.max(1000000),
        Validators.pattern(/^\d+$/),
      ],
    }),
  });
  ngOnInit() {
    this.load();

    // Search again on page 1 after the user pauses typing.
    this.search.valueChanges
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe(() => this.load());
  }

  pages() {
    return Math.max(1, Math.ceil(this.data().totalCount / 12));
  }
  load(page = 1) {
    // Cancel the previous list request so only the latest results are displayed.
    this.listRequest?.unsubscribe();
    this.error.set("");
    this.listRequest = this.http
      .get<Page<Product>>("/api/products/admin", {
        params: { search: this.search.value, pageSize: 12, pageNumber: page },
      })
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (data) => this.data.set(data),
        error: (e) =>
          this.error.set(e.error?.title ?? "Could not load products."),
      });
  }
  edit(product: Product) {
    // Retain the original DTO/version separately; patchValue fills only matching form controls.
    this.editing.set(product);
    this.form.patchValue(product);
    this.success.set("");
  }
  reset() {
    this.editing.set(null);
    this.form.reset({
      name: "",
      sku: "",
      description: "",
      price: 1,
      stockQuantity: 0,
    });
  }
  save() {
    // Show validation for untouched fields; busy prevents duplicate clicks in this UI.
    // This is not an API idempotency guarantee or a replacement for server validation.
    this.form.markAllAsTouched();
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    const id = this.editing()?.id;
    // Send the version the editor originally loaded. On 409, reload instead of silently
    // overwriting another user's stock/price changes with stale form values.
    // getRawValue includes disabled controls too; keep all request fields deliberate.
    const input = {
      ...this.form.getRawValue(),
      rowVersion: this.editing()?.rowVersion,
    };
    // HttpClient Observables are cold: choosing PUT/POST does not send until subscribe().
    let request: Observable<unknown>;
    if (id) {
      request = this.http.put(`/api/products/${id}`, input);
    } else {
      request = this.http.post("/api/products", input);
    }
    request.pipe(takeUntilDestroyed(this.destroy)).subscribe({
      next: () => {
        this.busy.set(false);
        this.success.set("Product saved.");
        this.reset();
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.error.set(e.error?.title ?? "Save failed.");
      },
    });
  }
  deactivate(product: Product) {
    // Soft deactivation preserves order history; the version also protects this write.
    this.busy.set(true);
    this.http
      .delete(`/api/products/${product.id}`, {
        params: { rowVersion: product.rowVersion },
      })
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.success.set(
            "Product deactivated. Historical orders are preserved.",
          );
          this.load();
        },
        error: (e) => {
          this.busy.set(false);
          this.error.set(e.error?.title ?? "Deactivation failed.");
        },
      });
  }
}
