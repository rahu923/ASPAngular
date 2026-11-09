import { inject } from "@angular/core";
import { CanActivateFn, Router } from "@angular/router";
import { Auth } from "./auth";
// Return true to open the page, or a URL telling Angular where to redirect.
export const signedInGuard: CanActivateFn = (_, state) => {
  const auth = inject(Auth);
  const router = inject(Router);

  if (auth.signedIn()) {
    return true;
  }

  return router.createUrlTree(["/account/login"], {
    queryParams: { returnUrl: state.url },
  });
};

export const permissionGuard: CanActivateFn = (route) => {
  const auth = inject(Auth);
  const router = inject(Router);
  let allowed: boolean;

  if (route.data["admin"]) {
    allowed = auth.isAdmin();
  } else {
    allowed = auth.has(route.data["permission"]);
  }

  if (allowed) {
    return true;
  }

  // The API also checks permissions; hiding a page alone is not security.
  return router.createUrlTree(["/access-denied"]);
};
