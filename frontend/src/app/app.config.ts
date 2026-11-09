import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
} from "@angular/core";
import {
  provideHttpClient,
  withInterceptors,
  withXsrfConfiguration,
} from "@angular/common/http";
import { provideRouter } from "@angular/router";
import { firstValueFrom } from "rxjs";
import { Auth } from "./core/auth";
import { apiInterceptor } from "./core/http";
import { permissionGuard, signedInGuard } from "./core/guards";

function restoreSession() {
  const auth = inject(Auth);
  // Angular waits for this Promise before starting the app.
  return firstValueFrom(auth.restore());
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideHttpClient(
      // Functional interceptors run in Angular's injection context. The custom interceptor
      // scopes credentials/retries to /api; built-in XSRF uses these agreed cookie/header names.
      withInterceptors([apiInterceptor]),
      withXsrfConfiguration({
        cookieName: "XSRF-TOKEN",
        headerName: "X-XSRF-TOKEN",
      }),
    ),
    // Wait for an Observable's first result before initial navigation, preventing guards
    // from treating a reload as anonymous before /me/refresh restoration has completed.
    provideAppInitializer(restoreSession),
    provideRouter([
      { path: "", pathMatch: "full", redirectTo: "products" },
      {
        path: "products",
        // Dynamic import creates a lazy feature chunk; this page need not be in the initial bundle.
        loadComponent: () =>
          import("./features/products/products").then((m) => m.Products),
      },
      // Each account URL uses the same component with a different form mode.
      {
        path: "account/login",
        data: { mode: "login" },
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "account/register",
        data: { mode: "register" },
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "account/forgot-password",
        data: { mode: "forgot-password" },
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "account/reset-password",
        data: { mode: "reset-password" },
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "account/confirm-email",
        data: { mode: "confirm-email" },
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "account/resend-confirmation",
        data: { mode: "resend-confirmation" },
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "account/change-password",
        data: { mode: "change-password" },
        canActivate: [signedInGuard],
        loadComponent: () =>
          import("./features/account/account").then((m) => m.Account),
      },
      {
        path: "orders",
        canActivate: [signedInGuard],
        loadComponent: () =>
          import("./features/orders/orders").then((m) => m.Orders),
      },
      {
        // Keep literal 'new' before ':id', or the router would interpret 'new' as an order ID.
        path: "orders/new",
        canActivate: [signedInGuard],
        loadComponent: () =>
          import("./features/orders/create").then((m) => m.CreateOrder),
      },
      {
        path: "orders/:id",
        canActivate: [signedInGuard],
        loadComponent: () =>
          import("./features/orders/detail").then((m) => m.OrderDetail),
      },
      {
        path: "admin/products",
        canActivate: [signedInGuard, permissionGuard],
        data: { permission: "ManageProducts" },
        loadComponent: () =>
          import("./features/admin/products").then((m) => m.ProductAdmin),
      },
      {
        path: "admin/users",
        canActivate: [signedInGuard, permissionGuard],
        data: { admin: true },
        loadComponent: () =>
          import("./features/admin/users").then((m) => m.Users),
      },
      {
        path: "access-denied",
        loadComponent: () =>
          import("./access-denied").then((m) => m.AccessDenied),
      },
      {
        // First-match routing: the catch-all belongs last or it would hide valid routes.
        path: "**",
        loadComponent: () => import("./not-found").then((m) => m.NotFound),
      },
    ]),
  ],
};
