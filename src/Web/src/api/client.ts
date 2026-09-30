import createClient from "openapi-fetch";
import type { paths } from "./schema.gen";

/**
 * Typed client for the aggregator's REST API, generated from its OpenAPI document (npm run generate:api).
 * Same origin: the dev server and nginx proxy /api, so the session cookie is sent without CORS.
 */
export const api = createClient<paths>({ baseUrl: "/", credentials: "same-origin" });

/** RFC 9457 problem details, as ProblemDetailsExceptionHandler writes them. */
export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
}

/** A non-2xx answer from the aggregator. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails | undefined,
  ) {
    super(problem?.detail ?? problem?.title ?? `Request failed with status ${status}`);
    this.name = "ApiError";
  }
}

/** Returns the data of a successful call and throws ApiError otherwise; use it in query and mutation functions. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok) {
    throw new ApiError(result.response.status, result.error as ProblemDetails | undefined);
  }

  return result.data as T;
}

export function isUnauthorized(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401;
}
