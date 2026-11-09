import { Component, DestroyRef, OnInit, inject, signal } from "@angular/core";
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
  ValidatorFn,
} from "@angular/forms";
import { ActivatedRoute, Router, RouterLink } from "@angular/router";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Auth } from "../../core/auth";
import { Feedback } from "../../shared/ui";
import { Observable } from "rxjs";

const accountTitles: Record<string, string> = {
  login: "Sign in",
  register: "Create account",
  "forgot-password": "Send reset link",
  "reset-password": "Reset password",
  "change-password": "Change password",
  "confirm-email": "Confirm email",
  "resend-confirmation": "Resend confirmation",
};

export function strongPassword(
  control: AbstractControl,
): ValidationErrors | null {
  // Angular validators return null for valid input or a named error map for the template.
  // This improves feedback; Identity remains the authoritative server password validator.
  const value = String(control.value ?? "");
  const longEnough = value.length >= 12;
  const hasLowercase = /[a-z]/.test(value);
  const hasUppercase = /[A-Z]/.test(value);
  const hasNumber = /\d/.test(value);
  const hasSymbol = /[^a-zA-Z0-9]/.test(value);

  if (longEnough && hasLowercase && hasUppercase && hasNumber && hasSymbol) {
    return null;
  }
  return { passwordStrength: true };
}
@Component({
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, Feedback],
  templateUrl: "./account.component.html",
})
export class Account implements OnInit {
  private readonly auth = inject(Auth);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroy = inject(DestroyRef);
  // Several lazy routes reuse this form component; static route data selects the operation.
  // snapshot is a one-time read, suitable for this component's initialization contract.
  readonly mode = this.route.snapshot.data["mode"] as string;
  readonly title = accountTitles[this.mode];
  readonly needsPassword = [
    "login",
    "register",
    "reset-password",
    "change-password",
  ].includes(this.mode);
  // URL fragments are not sent in the HTTP request path. Capture the recovery token
  // in memory, then remove the fragment below; never put it in localStorage or logs.
  private readonly fragment = new URLSearchParams(
    this.route.snapshot.fragment ?? "",
  );
  private readonly token = this.fragment.get("token") ?? "";
  readonly form = new FormGroup({
    email: new FormControl(this.fragment.get("email") ?? "", {
      nonNullable: true,
      validators:
        this.mode === "change-password"
          ? []
          : [Validators.required, Validators.email],
    }),
    password: new FormControl("", {
      nonNullable: true,
      validators: this.passwordValidators(),
    }),
    currentPassword: new FormControl("", {
      nonNullable: true,
      validators: this.mode === "change-password" ? [Validators.required] : [],
    }),
  });
  readonly error = signal("");
  readonly success = signal("");
  readonly busy = signal(false);
  ngOnInit() {
    // Remove the token-bearing fragment from browser history after capturing it in memory.
    if (this.route.snapshot.fragment) {
      void this.router.navigate([], {
        relativeTo: this.route,
        fragment: undefined,
        replaceUrl: true,
      });
    }
  }

  private passwordValidators(): ValidatorFn[] {
    if (!this.needsPassword) {
      return [];
    }
    // Existing passwords may follow older rules, so only require a value at login.
    if (this.mode === "login") {
      return [Validators.required];
    }
    return [Validators.required, strongPassword];
  }

  submit() {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    this.success.set("");
    const request = this.createRequest();
    request.pipe(takeUntilDestroyed(this.destroy)).subscribe({
      next: () => {
        this.busy.set(false);
        this.handleSuccess();
      },
      error: (error) => {
        this.busy.set(false);
        this.error.set(error.error?.title ?? "Request failed. Try again.");
      },
    });
  }

  private createRequest(): Observable<unknown> {
    const { email, password, currentPassword } = this.form.getRawValue();

    if (this.mode === "login") {
      return this.auth.login(email, password);
    }
    if (this.mode === "change-password") {
      return this.auth.submit(this.mode, { currentPassword, newPassword: password });
    }
    return this.auth.submit(this.mode, { email, password, token: this.token });
  }

  private handleSuccess() {
    if (this.mode === "login") {
      this.goToReturnUrl();
      return;
    }
    if (this.mode === "change-password" || this.mode === "reset-password") {
      this.auth.clear();
      this.success.set("Password updated. Sign in again.");
      this.form.reset();
      return;
    }
    if (this.mode === "confirm-email") {
      this.success.set("Email confirmed. You can sign in.");
      return;
    }
    this.success.set("If the account is eligible, check your email for the next step.");
  }

  private goToReturnUrl() {
    const target = this.route.snapshot.queryParamMap.get("returnUrl");
    // Only allow a path inside this application after login.
    if (target?.startsWith("/") && !target.startsWith("//")) {
      void this.router.navigateByUrl(target);
      return;
    }
    void this.router.navigateByUrl("/products");
  }
}
