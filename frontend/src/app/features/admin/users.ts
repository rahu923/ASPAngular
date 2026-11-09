import { Component, DestroyRef, OnInit, inject, signal } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { FormControl, ReactiveFormsModule } from "@angular/forms";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Subscription, debounceTime, distinctUntilChanged } from "rxjs";
import { Page } from "../products/product-api";
import { Feedback, Pager } from "../../shared/ui";
import { Auth } from "../../core/auth";
interface AdminUser {
  id: string;
  email: string;
  emailConfirmed: boolean;
  locked: boolean;
  roles: string[];
}
@Component({
  standalone: true,
  imports: [ReactiveFormsModule, Feedback, Pager],
  templateUrl: "./users.component.html",
})
export class Users implements OnInit {
  readonly auth = inject(Auth);
  private readonly http = inject(HttpClient);
  private readonly destroy = inject(DestroyRef);
  private listRequest?: Subscription;
  readonly search = new FormControl("", { nonNullable: true });
  // Available role names come from the backend; never manufacture an elevated role in the UI.
  readonly roles = signal<string[]>([]);
  readonly data = signal<Page<AdminUser>>({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 12,
  });
  readonly busy = signal(false);
  readonly error = signal("");
  readonly success = signal("");
  ngOnInit() {
    this.loadRoles();
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

  private loadRoles() {
    this.http
      .get<string[]>("/api/admin/roles")
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (roles) => this.roles.set(roles),
        error: () => this.error.set("Could not load roles."),
      });
  }

  pages() {
    return Math.max(1, Math.ceil(this.data().totalCount / 12));
  }
  load(page = 1) {
    // Cancel the previous list request so only the latest results are displayed.
    this.listRequest?.unsubscribe();
    this.error.set("");
    this.listRequest = this.http
      .get<Page<AdminUser>>("/api/admin/users", {
        params: { search: this.search.value, pageNumber: page, pageSize: 12 },
      })
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: (data) => this.data.set(data),
        error: (e) => this.error.set(e.error?.title ?? "Could not load users."),
      });
  }
  assign(user: AdminUser, role: string) {
    this.update(user, "role", { role });
  }
  lock(user: AdminUser) {
    this.update(user, "lock", { locked: !user.locked });
  }
  private update(user: AdminUser, operation: string, input: object) {
    // Update through the Admin-only endpoint, then re-read authoritative server state.
    // Existing signed access claims can outlive a role change until their short expiry.
    this.busy.set(true);
    this.error.set("");
    this.http
      .put(`/api/admin/users/${user.id}/${operation}`, input)
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.success.set("User updated.");
          this.load(this.data().pageNumber);
        },
        error: (e) => {
          this.busy.set(false);
          this.error.set(e.error?.title ?? "Update failed.");
        },
      });
  }
}
