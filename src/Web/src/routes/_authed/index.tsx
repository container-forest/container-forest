import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_authed/")({
  component: Home,
});

function Home() {
  return (
    <>
      <h1 className="font-heading text-2xl font-semibold">container-forest</h1>
      <p className="text-muted-foreground mt-2">Панель у розробці: екрани з'являться тут.</p>
    </>
  );
}
