import type { ApiErrorResponse } from './contracts';

export type ApiErrorKind =
  | 'validation'
  | 'unauthenticated'
  | 'forbidden'
  | 'notFound'
  | 'conflict'
  | 'notReady'
  | 'external'
  | 'network'
  | 'unexpected';

export type ValidationErrors = Readonly<Record<string, readonly string[]>>;

const safeStatusMessages: Record<number, string> = {
  400: 'Some information was not accepted. Review the form and try again.',
  401: 'Your session has ended. Sign in again to continue.',
  403: 'You do not have access to perform this action.',
  404: 'The requested information could not be found.',
  409: 'The information changed before this action completed. Refresh and try again.',
  422: 'Some information was not accepted. Review the form and try again.',
  429: 'The provider rate limit was reached. Wait and try again.',
  502: 'An external provider could not complete the request.',
  503: 'Engineering Intake Gate is not ready for this operation yet.',
  504: 'An external provider timed out. Try again later.',
};

const kindForResponse = (status: number, code: string, hasValidationErrors: boolean): ApiErrorKind => {
  if (code === 'InvalidAntiforgeryToken') return 'unauthenticated';
  if (code === 'ProductionNotAuthorized') return 'forbidden';
  if (hasValidationErrors) return 'validation';
  if (status === 401) return 'unauthenticated';
  if (status === 403) return 'forbidden';
  if ([429, 502, 504].includes(status) || /Provider|AuthenticationFailed|AuthorizationFailed|RateLimited|Timeout/.test(code)) return 'external';
  if (status === 400 || status === 422) return 'validation';
  if (status === 404) return 'notFound';
  if (status === 409) return 'conflict';
  if (status === 503) return 'notReady';
  return 'unexpected';
};

export class ApiError extends Error {
  public readonly kind: ApiErrorKind;
  public readonly status: number | null;
  public readonly code: string;
  public readonly details: readonly string[];
  public readonly fieldErrors: ValidationErrors;
  public readonly sectionErrors: ValidationErrors;

  public constructor(
    kind: ApiErrorKind,
    message: string,
    options: {
      status?: number;
      code?: string;
      details?: readonly string[];
      fieldErrors?: ValidationErrors;
      sectionErrors?: ValidationErrors;
    } = {},
  ) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = options.status ?? null;
    this.code = options.code ?? 'RequestFailed';
    this.details = options.details ?? [];
    this.fieldErrors = options.fieldErrors ?? {};
    this.sectionErrors = options.sectionErrors ?? {};
  }

  public static network(): ApiError {
    return new ApiError(
      'network',
      'The backend is unavailable. Check the service and try again.',
      { code: 'BackendUnavailable' },
    );
  }

  public static fromResponse(status: number, body: Partial<ApiErrorResponse> | null): ApiError {
    const fallback = safeStatusMessages[status] ?? 'The request could not be completed safely.';
    const message = typeof body?.message === 'string' && body.message.trim() ? body.message : fallback;
    const code = typeof body?.error === 'string' ? body.error : `Http${status}`;
    const fieldErrors = normalizeValidationErrors(body?.fieldErrors);
    const sectionErrors = normalizeValidationErrors(body?.sectionErrors);
    return new ApiError(kindForResponse(status, code,
      Object.keys(fieldErrors).length > 0 || Object.keys(sectionErrors).length > 0), message, {
      status,
      code,
      details: Array.isArray(body?.details) ? body.details.filter((item): item is string => typeof item === 'string') : [],
      fieldErrors,
      sectionErrors,
    });
  }

  public withoutField(field: string): ApiError {
    if (!(field in this.fieldErrors) && !(field in this.sectionErrors)) return this;
    const fieldErrors = { ...this.fieldErrors };
    const sectionErrors = { ...this.sectionErrors };
    delete fieldErrors[field];
    delete sectionErrors[field];
    return new ApiError(this.kind, this.message, {
      ...(this.status === null ? {} : { status: this.status }),
      code: this.code,
      details: this.details,
      fieldErrors,
      sectionErrors,
    });
  }
}

const normalizeValidationErrors = (value: unknown): ValidationErrors => {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return {};
  return Object.fromEntries(Object.entries(value).flatMap(([key, messages]) => {
    if (!Array.isArray(messages)) return [];
    const safe = messages.filter((message): message is string => typeof message === 'string' && Boolean(message.trim()));
    return safe.length ? [[key, safe]] : [];
  }));
};

export const asApiError = (error: unknown): ApiError =>
  error instanceof ApiError
    ? error
    : new ApiError('unexpected', "We couldn't complete this action. Try again.", { code: 'UnexpectedClientError' });
