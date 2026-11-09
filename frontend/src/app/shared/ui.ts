import {
  Component,
  Directive,
  TemplateRef,
  ViewContainerRef,
  effect,
  inject,
  input,
  output,
} from "@angular/core";
import { Auth } from "../core/auth";
@Component({
  selector: "app-feedback",
  standalone: true,
  templateUrl: "./feedback.component.html",
})
export class Feedback {
  readonly error = input("");
  readonly success = input("");
}
@Component({
  selector: "app-pager",
  standalone: true,
  templateUrl: "./pager.component.html",
})
export class Pager {
  // Input signals are read by the child; the child emits intent rather than mutating
  // its parent's pagination state. The parent decides whether/how to fetch the new page.
  readonly page = input(1);
  readonly pages = input(1);
  readonly change = output<number>();
}
@Directive({ selector: "[appPermission]", standalone: true })
export class Permission {
  // *appPermission is a structural directive: TemplateRef is its unevaluated content,
  // and ViewContainerRef is the place where an authorized UI view can be inserted.
  // It only controls presentation; API policies enforce actual access.
  readonly appPermission = input.required<string>();
  private readonly auth = inject(Auth);
  private readonly template = inject(TemplateRef);
  private readonly view = inject(ViewContainerRef);
  constructor() {
    // effect tracks the permission input and Auth signals it reads, updating when the
    // account changes. Clear first to avoid accumulating duplicate embedded views.
    effect(() => {
      this.view.clear();
      if (this.hasPermission()) {
        this.view.createEmbeddedView(this.template);
      }
    });
  }

  private hasPermission() {
    const permission = this.appPermission();
    if (permission === "Admin") {
      return this.auth.isAdmin();
    }
    return this.auth.has(permission);
  }
}
