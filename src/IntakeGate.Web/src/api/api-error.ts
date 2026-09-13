import type { ApiErrorResponse } from './contracts';

export type ApiErrorKind =
  | 'validation'
  | 'unauthenticated'
  | 'forbidden'
  | 'notFound'
  | 'conflict'
  | 'notReady'
  | 'network'
  | 'unexpected';

const safeStatusMessages: Record<number, string> = {
  400: 'Some information was not accepted. Review the form and try again.',
  401: 'Your session has ended. Sign in again to continue.',
  403: 'You do not have access to perform this action.',
  404: 'The requested information could not be found.',
  409: 'The information changed before this action completed. Refresh and try again.',
  503: 'Engineering Intake Gate is not ready for this operation yet.',
};

const kindForStatus = (status: number): ApiErrorKind => {
  if (status === 400) return 'validation';
  if (status === 401) return 'unauthenticated';
  if (status === 403) return 'forbidden';
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

  public constructor(
    kind: ApiErrorKind,
    message: string,
    options: { status?: number; code?: string; details?: readonly string[] } = {},
  ) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = options.status ?? null;
    this.code = options.code ?? 'RequestFailed';
    this.details = options.details ?? [];
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
    return new ApiError(kindForStatus(status), message, {
      status,
      code: typeof body?.error === 'string' ? body.error : `Http${status}`,
      details: Array.isArray(body?.details) ? body.details.filter((item): item is string => typeof item === 'string') : [],
    });
  }
}

export const asApiError = (error: unknown): ApiError =>
  error instanceof ApiError
    ? error
    : new ApiError('unexpected', 'Something went wrong. Try again.', { code: 'UnexpectedClientError' });
