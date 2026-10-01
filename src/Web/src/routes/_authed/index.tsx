import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

export const Route = createFileRoute("/_authed/")({
  component: Home,
});

function Home() {
  const { t } = useTranslation();

  return (
    <>
      <h1 className="font-heading text-2xl font-semibold">container-forest</h1>
      <p className="text-muted-foreground mt-2">{t("home.placeholder")}</p>
    </>
  );
}
