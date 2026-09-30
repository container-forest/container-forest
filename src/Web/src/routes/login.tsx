import { createFileRoute } from "@tanstack/react-router";
import { z } from "zod";

const searchSchema = z.object({
  // Where to go after signing in. Only same-site paths: an absolute URL here would be an open redirect.
  redirect: z
    .string()
    .refine((value) => value.startsWith("/") && !value.startsWith("//"))
    .optional()
    .catch(undefined),
});

export const Route = createFileRoute("/login")({
  validateSearch: searchSchema,
  component: Login,
});

// Placeholder until the login screen exists (Денисенко Л. В.): it posts to /api/auth/login and then
// navigates to search.redirect ?? "/".
function Login() {
  return (
    <main className="mx-auto max-w-md p-6">
      <h1 className="font-heading text-2xl font-semibold">Вхід</h1>
      <p className="text-muted-foreground mt-2">
        Вхід ще не реалізовано. Для розробки запустіть панель з <code>CF_DEV_USER=&lt;користувач&gt;</code>: dev-сервер
        додасть заголовок <code>X-Dev-User</code> до запитів у ЦА.
      </p>
    </main>
  );
}
