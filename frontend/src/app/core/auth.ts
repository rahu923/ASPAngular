import { Injectable, computed, inject, signal } from "@angular/core";
import {
  HttpClient,
  HttpContext,
  HttpContextToken,
  HttpErrorResponse,
} from "@angular/common/http";
import {
  Observable,
  catchError,
  finalize,
  map,
  of,
  shareReplay,
  switchMap,
  tap,
  throwError,
} from "rxjs";
export interface User {
  id: string;
  email: string;
  roles: string[];
  permissions: string[];
}
// Request-local metadata, not an HTTP header: internal auth calls opt out of retry logic
// so a failed refresh cannot recursively start another refresh.
export const SKIP_REFRESH = new HttpContextToken(() => false);
@Injectable({ providedIn: "root" })
export class Auth {
  private readonly http = inject(HttpClient);
  // Store user details only. HttpOnly JWT/refresh cookies are intentionally unreadable here.
  readonly user = signal<User | null>(null);
  readonly signedIn = computed(() => this.user() !== null);
  // Version of the successfully established session. The interceptor compares the value
  // captured at send time to detect late 401 responses from the previous access token.
  readonly epoch = signal(0);
  private refreshing?: Observable<User>;
  has(permission: string) {
    return this.user()?.permissions.includes(permission) ?? false;
  }
  isAdmin() {
    return this.user()?.roles.includes("Admin") ?? false;
  }
  clear() {
    this.user.set(null);
  }
  csrf() {
    return this.http.get<void>("/api/auth/csrf", {
      context: new HttpContext().set(SKIP_REFRESH, true),
    });
  }
  private me() {
    return this.http
      .get<User>("/api/auth/me", {
        context: new HttpContext().set(SKIP_REFRESH, true),
      })
      .pipe(tap((user) => this.user.set(user)));
  }
  restore(): Observable<User | null> {
    // App initialization waits for this: ask /me first, refresh only for a 401.
    // Anonymous/offline startup resolves to null rather than preventing the public app from loading.
    return this.me().pipe(
      catchError((error: HttpErrorResponse) => {
        if (error.status === 401) {
          return this.refresh().pipe(catchError(() => of(null)));
        }
        return of(null);
      }),
    );
  }
  refresh(): Observable<User> {
    // Cache the in-flight Observable itself: concurrent callers subscribe to the same
    // rotation, rather than consuming the same single-use cookie in separate HTTP requests.
    if (this.refreshing) {
      return this.refreshing;
    }

    this.refreshing = this.refreshSession().pipe(
      catchError((error) => {
        this.clear();
        return throwError(() => error);
      }),
      // Clear the saved request when it finishes, so a future refresh can start.
      finalize(() => {
        this.refreshing = undefined;
      }),
      // All waiting requests share one refresh, even if one caller leaves the page.
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.refreshing;
  }

  private refreshSession(): Observable<User> {
    // switchMap runs each HTTP step after the previous step succeeds:
    // CSRF token -> refresh cookies -> new CSRF token -> current user.
    return this.csrf().pipe(
      switchMap(() => this.requestRefresh()),
      switchMap(() => this.csrf()),
      switchMap(() => this.me()),
      tap(() => this.epoch.update((version) => version + 1)),
    );
  }

  private requestRefresh() {
    return this.http.post<void>(
      "/api/auth/refresh",
      {},
      { context: new HttpContext().set(SKIP_REFRESH, true) },
    );
  }
  login(email: string, password: string) {
    // Sequential requests matter: csrf before the unsafe login, then csrf again after
    // the server issues authenticated cookies. Preserve the user result with map().
    return this.csrf().pipe(
      switchMap(() =>
        this.http.post<User>("/api/auth/login", { email, password }),
      ),
      tap((user) => {
        this.user.set(user);
        this.epoch.update((x) => x + 1);
      }),
      switchMap((user) => this.csrf().pipe(map(() => user))),
    );
  }
  submit(endpoint: string, input: object) {
    return this.csrf().pipe(
      switchMap(() => this.http.post<unknown>(`/api/auth/${endpoint}`, input)),
    );
  }
  logout() {
    return this.submit("logout", {}).pipe(
      tap(() => this.clear()),
      switchMap(() => this.csrf()),
    );
  }
}
