import { TestBed } from "@angular/core/testing";
import {
  HttpClient,
  HttpXsrfTokenExtractor,
  provideHttpClient,
  withInterceptors,
} from "@angular/common/http";
import {
  HttpTestingController,
  provideHttpClientTesting,
} from "@angular/common/http/testing";
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  provideRouter,
} from "@angular/router";
import { Auth, User } from "./auth";
import { apiInterceptor, HttpState } from "./http";
import { permissionGuard, signedInGuard } from "./guards";
import { strongPassword } from "../features/account/account";
import { FormControl } from "@angular/forms";
const user: User = {
  id: "alice",
  email: "alice@test.local",
  roles: ["Customer"],
  permissions: [],
};
describe("Authentication and request coordination", () => {
  let auth: Auth;
  let http: HttpClient;
  let backend: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([apiInterceptor])),
        provideHttpClientTesting(),
        {
          provide: HttpXsrfTokenExtractor,
          useValue: { getToken: () => "csrf-test" },
        },
      ],
    });
    auth = TestBed.inject(Auth);
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });
  afterEach(() => backend.verify());
  function finishRefresh() {
    backend.expectOne("/api/auth/csrf").flush(null);
    const refresh = backend.expectOne("/api/auth/refresh");
    expect(refresh.request.headers.get("X-XSRF-TOKEN")).toBe("csrf-test");
    refresh.flush(null);
    backend.expectOne("/api/auth/csrf").flush(null);
    backend.expectOne("/api/auth/me").flush(user);
  }
  it("restores state using me without reading an authentication token", () => {
    auth.restore().subscribe();
    backend.expectOne("/api/auth/me").flush(user);
    expect(auth.user()).toEqual(user);
  });
  it("restores an expired session with refresh and clears state if refresh fails", () => {
    auth.user.set(user);
    auth.restore().subscribe();
    backend
      .expectOne("/api/auth/me")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    backend.expectOne("/api/auth/csrf").flush(null);
    backend
      .expectOne("/api/auth/refresh")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    expect(auth.user()).toBeNull();
  });
  it("shares one refresh for concurrent 401s and retries each request once", () => {
    let received = 0;
    http.get("/api/orders").subscribe(() => received++);
    http.get("/api/products/admin").subscribe(() => received++);
    backend
      .expectOne("/api/orders")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    backend
      .expectOne("/api/products/admin")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    finishRefresh();
    backend.expectOne("/api/orders").flush({});
    backend.expectOne("/api/products/admin").flush({});
    expect(received).toBe(2);
    expect(TestBed.inject(HttpState).pending()).toBe(0);
  });
  it("does not start another refresh for a late 401 from the previous access token", () => {
    http.get("/api/one").subscribe();
    http.get("/api/two").subscribe();
    const one = backend.expectOne("/api/one");
    const two = backend.expectOne("/api/two");
    one.flush({}, { status: 401, statusText: "Unauthorized" });
    finishRefresh();
    backend.expectOne("/api/one").flush({});
    two.flush({}, { status: 401, statusText: "Unauthorized" });
    backend.expectOne("/api/two").flush({});
    backend.expectNone("/api/auth/refresh");
  });
  it("does not refresh a retried request twice", () => {
    let failures = 0;
    http.get("/api/orders").subscribe({ error: () => failures++ });
    backend
      .expectOne("/api/orders")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    finishRefresh();
    backend
      .expectOne("/api/orders")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    expect(failures).toBe(1);
    backend.expectNone("/api/auth/refresh");
  });
  it("does not refresh 403 or login failures", () => {
    http.get("/api/admin/users").subscribe({ error: () => {} });
    backend
      .expectOne("/api/admin/users")
      .flush({}, { status: 403, statusText: "Forbidden" });
    http.post("/api/auth/login", {}).subscribe({ error: () => {} });
    backend
      .expectOne("/api/auth/login")
      .flush({}, { status: 401, statusText: "Unauthorized" });
    backend.expectNone("/api/auth/refresh");
  });
  it("attaches credentials and CSRF only to trusted relative API requests", () => {
    http.post("/api/orders", {}).subscribe();
    const api = backend.expectOne("/api/orders");
    expect(api.request.withCredentials).toBe(true);
    expect(api.request.headers.get("X-XSRF-TOKEN")).toBe("csrf-test");
    api.flush({});
    http.post("https://external.example/api/orders", {}).subscribe();
    const external = backend.expectOne("https://external.example/api/orders");
    expect(external.request.withCredentials).toBe(false);
    expect(external.request.headers.has("X-XSRF-TOKEN")).toBe(false);
    external.flush({});
  });
  it("guards redirect anonymous and unprivileged users, then accept required permission", () => {
    const route = {
      data: { permission: "ManageProducts" },
    } as unknown as ActivatedRouteSnapshot;
    const state = { url: "/orders" } as RouterStateSnapshot;
    const result = TestBed.runInInjectionContext(() =>
      signedInGuard(route, state),
    );
    expect(TestBed.inject(Router).serializeUrl(result as never)).toContain(
      "/account/login",
    );
    auth.user.set(user);
    expect(
      TestBed.runInInjectionContext(() => signedInGuard(route, state)),
    ).toBe(true);
    const denied = TestBed.runInInjectionContext(() =>
      permissionGuard(route, state),
    );
    expect(TestBed.inject(Router).serializeUrl(denied as never)).toBe(
      "/access-denied",
    );
    auth.user.set({ ...user, permissions: ["ManageProducts"] });
    expect(
      TestBed.runInInjectionContext(() => permissionGuard(route, state)),
    ).toBe(true);
  });
  it("validates the custom password requirements", () => {
    expect(strongPassword(new FormControl("weak"))).toEqual({
      passwordStrength: true,
    });
    expect(strongPassword(new FormControl("Strong!7aA-password"))).toBeNull();
  });
});
