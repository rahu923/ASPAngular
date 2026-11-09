import { Component, DestroyRef, inject, signal } from "@angular/core";
import { Router, RouterLink, RouterOutlet } from "@angular/router";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Auth } from "./core/auth";
import { HttpState } from "./core/http";
import { Permission } from "./shared/ui";
@Component({
  selector: "app-root",
  standalone: true,
  imports: [RouterLink, RouterOutlet, Permission],
  templateUrl: "./app.component.html",
})
export class App {
  // Public services are read by app.component.html.
  readonly auth = inject(Auth);
  readonly http = inject(HttpState);
  private readonly destroy = inject(DestroyRef);
  private readonly router = inject(Router);
  readonly error = signal("");

  logout() {
    this.error.set("");
    this.auth
      .logout()
      // Automatically stop listening if this component is destroyed.
      .pipe(takeUntilDestroyed(this.destroy))
      .subscribe({
        next: () => {
          void this.router.navigate(["/account/login"]);
        },
        error: () => {
          this.error.set(
            "Sign out failed. Check your connection and try again.",
          );
        },
      });
  }
}
