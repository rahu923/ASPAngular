import { Injectable, inject, signal } from "@angular/core";
import {
  HttpErrorResponse,
  HttpInterceptorFn,
  HttpRequest,
  HttpXsrfTokenExtractor,
} from "@angular/common/http";
import { catchError, finalize, switchMap, throwError } from "rxjs";
import { Auth, SKIP_REFRESH } from "./auth";
@Injectable({ providedIn: "root" })
export class HttpState {
  // A counter, not a boolean, because multiple HTTP requests may overlap.
  readonly pending = signal(0);
  readonly error = signal("");
}
export function trustedApi(url: string) {
  // A deliberately narrow trust boundary for this same-origin deployment. Do not attach
  // credentials/CSRF to an arbitrary absolute URL supplied by another feature or library.
  return url.startsWith("/api/") && !url.startsWith("//");
}

function prepareApiRequest(
  request: HttpRequest<unknown>,
  xsrf: HttpXsrfTokenExtractor,
) {
  // HTTP requests cannot be changed directly, so return a modified copy.
  let headers = request.headers;
  const token = xsrf.getToken();
  const readOnlyRequest = ["GET", "HEAD", "OPTIONS"].includes(request.method);

  if (!readOnlyRequest && token) {
    headers = headers.set("X-XSRF-TOKEN", token);
  }

  return request.clone({ withCredentials: true, headers });
}

export const apiInterceptor: HttpInterceptorFn = (request, next) => {
  if (!trustedApi(request.url)) {
    return next(request);
  }
  const auth = inject(Auth);
  const state = inject(HttpState);
  const xsrf = inject(HttpXsrfTokenExtractor);
  const sessionVersion = auth.epoch();
  state.pending.update((n) => n + 1);
  return next(prepareApiRequest(request, xsrf)).pipe(
    catchError((error: HttpErrorResponse) => {
      // Only an eligible 401 suggests expired authentication. A 403 means authorization
      // denied, and refreshing unchanged permissions must not create a retry loop.
      if (
        error.status !== 401 ||
        request.context.get(SKIP_REFRESH) ||
        request.url.startsWith("/api/auth/")
      ) {
        return throwError(() => error);
      }
      // Another request may already have completed refresh while this older request
      // was still in flight. Retry using the updated cookies without rotating again.
      if (auth.epoch() !== sessionVersion) {
        return next(prepareApiRequest(request, xsrf));
      }
      // next() continues downstream; it does not re-enter this interceptor. Therefore
      // an original request retries at most once, even if the retry also receives 401.
      return auth.refresh().pipe(
        switchMap(() => next(prepareApiRequest(request, xsrf))),
      );
    }),
    catchError((error: HttpErrorResponse) => {
      if (!request.url.startsWith("/api/auth/")) {
        state.error.set(
          error.error?.title ?? "Request failed. Check your connection.",
        );
      }
      return throwError(() => error);
    }),
    // Runs on success, error AND unsubscription so a cancelled search cannot stick the loader on.
    finalize(() => state.pending.update((n) => n - 1)),
  );
};
