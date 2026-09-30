import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_authed/")({
  component: Home,
});

function Home() {
  return (
    <>
      <h1>container-forest</h1>
      <p>Панель у розробці: екрани з'являться тут.</p>
    </>
  );
}
