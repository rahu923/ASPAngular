import { Injectable, inject } from "@angular/core";
import { HttpClient } from "@angular/common/http";
export interface Product {
  id: number;
  name: string;
  sku: string;
  description: string;
  price: number;
  stockQuantity: number;
  isActive: boolean;
  rowVersion: string;
}
export interface Page<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}
@Injectable({ providedIn: "root" })
export class ProductApi {
  private readonly http = inject(HttpClient);
  list(search: string, sort: string, pageNumber: number) {
    return this.http.get<Page<Product>>("/api/products", {
      params: { search, sort, pageNumber, pageSize: 12 },
    });
  }
}
