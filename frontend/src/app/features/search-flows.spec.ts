import { Signal, Type } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { FormControl } from "@angular/forms";
import { provideHttpClient } from "@angular/common/http";
import {
  HttpTestingController,
  provideHttpClientTesting,
} from "@angular/common/http/testing";
import { provideRouter } from "@angular/router";
import { vi } from "vitest";
import { ProductAdmin } from "./admin/products";
import { Users } from "./admin/users";
import { Orders } from "./orders/orders";
import { CreateOrder } from "./orders/create";

// Both admin screens expose the same search and pagination operations.
interface AdminSearchScreen {
  search: FormControl<string>;
  data: Signal<{ pageNumber: number }>;
  error: Signal<string>;
  load(page?: number): void;
}

const adminScreens: {
  name: string;
  component: Type<AdminSearchScreen>;
  url: string;
}[] = [
  { name: "product admin", component: ProductAdmin, url: "/api/products/admin" },
  { name: "user admin", component: Users, url: "/api/admin/users" },
];

function page(pageNumber = 1) {
  return { items: [], totalCount: 36, pageNumber, pageSize: 12 };
}

beforeEach(() => {
  vi.useFakeTimers();
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
});

afterEach(() => {
  TestBed.inject(HttpTestingController).verify();
  vi.useRealTimers();
});

for (const screen of adminScreens) {
  describe(`${screen.name} search`, () => {
    it("cancels old page requests, debounces search, and returns to page 1", () => {
      const fixture = TestBed.createComponent(screen.component);
      fixture.detectChanges();
      const backend = TestBed.inject(HttpTestingController);
      // The user admin screen also loads its available roles.
      for (const request of backend.match("/api/admin/roles")) {
        request.flush([]);
      }
      backend.expectOne((request) => request.url === screen.url).flush(page());

      fixture.componentInstance.load(2);
      const oldPage = backend.expectOne((request) => request.url === screen.url);
      fixture.componentInstance.search.setValue("desk");
      vi.advanceTimersByTime(299);
      backend.expectNone((request) => request.url === screen.url);
      vi.advanceTimersByTime(1);

      expect(oldPage.cancelled).toBe(true);
      const search = backend.expectOne((request) => request.url === screen.url);
      expect(search.request.params.get("search")).toBe("desk");
      expect(search.request.params.get("pageNumber")).toBe("1");
      search.flush(page());
      expect(fixture.componentInstance.data().pageNumber).toBe(1);
      fixture.destroy();
    });

    it("can search after a failed request and cleans up when leaving the page", () => {
      const fixture = TestBed.createComponent(screen.component);
      fixture.detectChanges();
      const backend = TestBed.inject(HttpTestingController);
      for (const request of backend.match("/api/admin/roles")) {
        request.flush([]);
      }
      backend.expectOne((request) => request.url === screen.url)
        .flush({}, { status: 500, statusText: "Server error" });
      expect(fixture.componentInstance.error()).not.toBe("");

      fixture.componentInstance.search.setValue("retry");
      vi.advanceTimersByTime(300);
      const retry = backend.expectOne((request) => request.url === screen.url);
      expect(fixture.componentInstance.error()).toBe("");
      fixture.destroy();
      expect(retry.cancelled).toBe(true);

      fixture.componentInstance.search.setValue("after leaving");
      vi.advanceTimersByTime(300);
      backend.expectNone((request) => request.url === screen.url);
    });
  });
}

describe("orders search", () => {
  it("uses current filters, cancels stale pages, and recovers from errors", () => {
    const fixture = TestBed.createComponent(Orders);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const backend = TestBed.inject(HttpTestingController);
    const initial = backend.expectOne((request) => request.url === "/api/orders");
    expect(initial.request.params.get("sort")).toBe("newest");
    initial.flush(page());

    component.loadOrders(2);
    const secondPage = backend.expectOne((request) => request.url === "/api/orders");
    component.status.setValue("Draft");
    expect(secondPage.cancelled).toBe(true);
    const filtered = backend.expectOne((request) => request.url === "/api/orders");
    expect(filtered.request.params.get("pageNumber")).toBe("1");
    expect(filtered.request.params.get("status")).toBe("Draft");
    filtered.flush({}, { status: 500, statusText: "Server error" });
    expect(component.loading()).toBe(false);
    expect(component.error()).not.toBe("");

    component.search.setValue("desk");
    vi.advanceTimersByTime(300);
    const retry = backend.expectOne((request) => request.url === "/api/orders");
    expect(retry.request.params.get("search")).toBe("desk");
    expect(retry.request.params.get("status")).toBe("Draft");
    retry.flush(page());
    expect(component.error()).toBe("");
    expect(component.loading()).toBe(false);
    fixture.destroy();
  });
});

describe("order creation catalog", () => {
  it("cancels an old page when a search starts and updates pagination", () => {
    const fixture = TestBed.createComponent(CreateOrder);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const backend = TestBed.inject(HttpTestingController);
    backend.expectOne((request) => request.url === "/api/products").flush(page());

    component.loadPage(2);
    const oldPage = backend.expectOne((request) => request.url === "/api/products");
    component.search.setValue("desk");
    vi.advanceTimersByTime(300);
    expect(oldPage.cancelled).toBe(true);
    const search = backend.expectOne((request) => request.url === "/api/products");
    expect(search.request.params.get("pageNumber")).toBe("1");
    search.flush({ ...page(), totalCount: 13 });
    expect(component.productPage()).toBe(1);
    expect(component.productPages()).toBe(2);

    component.loadPage(2);
    const active = backend.expectOne((request) => request.url === "/api/products");
    fixture.destroy();
    expect(active.cancelled).toBe(true);
  });
});
