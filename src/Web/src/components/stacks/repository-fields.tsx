import { useQuery } from "@tanstack/react-query";
import { Controller, type UseFormReturn, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { gitConnectionsQuery } from "@/api/queries";
import { ErrorText, FieldError } from "@/components/form-messages";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { PUBLIC_REPOSITORY, type RepositoryForm, prefixMissedBy } from "@/features/stacks/model";

/**
 * Name, connection and address of a repository; shared by "Add repository" and "Edit repository". The connections
 * are those the user may use (ADR-0006). Returns, through the form, whether the address misses the connection's prefix.
 */
export function RepositoryFields({ idPrefix, form }: { idPrefix: string; form: UseFormReturn<RepositoryForm> }) {
  const { t } = useTranslation();
  const connections = useQuery(gitConnectionsQuery);
  const {
    register,
    control,
    formState: { errors },
  } = form;
  const [url, connectionId] = useWatch({ control, name: ["url", "connectionId"] });
  const missed = prefixMissedBy({ url, connectionId }, connections.data ?? []);

  return (
    <>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-name`}>{t("repositories.form.name")}</Label>
        <Input
          id={`${idPrefix}-name`}
          autoComplete="off"
          aria-invalid={errors.name ? true : undefined}
          aria-describedby={errors.name ? `${idPrefix}-name-error` : undefined}
          {...register("name")}
        />
        <FieldError id={`${idPrefix}-name-error`} message={errors.name?.message} />
      </div>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-connection`}>{t("repositories.form.connection")}</Label>
        <Controller
          control={control}
          name="connectionId"
          render={({ field }) => (
            // Radix calls onValueChange with "" while the value matches no item yet (connections still loading).
            <Select value={field.value} onValueChange={(value) => value !== "" && field.onChange(value)}>
              <SelectTrigger id={`${idPrefix}-connection`} aria-describedby={`${idPrefix}-connection-hint`}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={PUBLIC_REPOSITORY}>{t("repositories.form.public")}</SelectItem>
                {(connections.data ?? []).map((connection) => (
                  <SelectItem key={connection.id} value={connection.id}>
                    {connection.name} — {connection.urlPrefix}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        />
        <p id={`${idPrefix}-connection-hint`} className="text-muted-foreground text-sm">
          {t("repositories.form.connectionHint")}
        </p>
        {connections.isError && <ErrorText>{t("repositories.form.connectionsFailed")}</ErrorText>}
      </div>
      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-url`}>{t("repositories.form.url")}</Label>
        <Input
          id={`${idPrefix}-url`}
          type="url"
          autoComplete="off"
          placeholder="https://gitea.example.com/team/app.git"
          aria-invalid={errors.url || missed ? true : undefined}
          aria-describedby={errors.url ? `${idPrefix}-url-error` : missed ? `${idPrefix}-url-prefix` : undefined}
          {...register("url")}
        />
        <FieldError id={`${idPrefix}-url-error`} message={errors.url?.message} />
        {missed && !errors.url && (
          <p id={`${idPrefix}-url-prefix`} className="text-destructive text-sm">
            {t("repositories.form.outsidePrefix", { prefix: missed })}
          </p>
        )}
      </div>
    </>
  );
}

/** Whether the form may be sent: the address must lie under the chosen connection's prefix. */
export function useRepositoryFormBlocked(form: UseFormReturn<RepositoryForm>): boolean {
  const connections = useQuery(gitConnectionsQuery);
  const [url, connectionId] = useWatch({ control: form.control, name: ["url", "connectionId"] });
  return prefixMissedBy({ url, connectionId }, connections.data ?? []) !== null;
}
